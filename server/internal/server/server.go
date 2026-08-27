// Package server is the authoritative game server: a WebSocket gateway on
// /ws (PROTOCOL.md) feeding a fixed-tick (20 Hz) simulation. The world is an
// in-memory map of entities, one per connected player in M1; the tick loop
// steps every entity against the shared terrain field and fans out one full
// snapshot per tick, patching the per-client ack_seq into a shared body.
package server

import (
	"context"
	"encoding/binary"
	"fmt"
	"log"
	"math"
	"math/rand"
	"net/http"
	"sort"
	"sync"
	"time"

	"github.com/gorilla/websocket"

	"space-adventure/server/internal/defs"
	"space-adventure/server/internal/protocol"
	"space-adventure/server/internal/sim"
	"space-adventure/server/internal/store"
	"space-adventure/server/internal/terrain"
)

// Tunables (PROTOCOL "Semantics": heartbeat; GDD tick rate).
const (
	// silentTimeout is how long a connection may go without any inbound
	// message before the server drops it (PROTOCOL: "drops connections
	// silent for 10 s"). The client pings every 2 s when idle, so a live
	// client never trips it.
	silentTimeout = 10 * time.Second
	// writeTimeout bounds a single outbound write; a client that stops
	// draining its socket is dead.
	writeTimeout = 10 * time.Second
	// outQueue is the per-connection outbound buffer. Snapshots are full
	// state, so when the queue is full the newest snapshot is dropped
	// rather than blocking the tick loop.
	outQueue = 32
	// worldEntityIDBase is where zone-placed NPC/target entity ids start
	// (see New): far above where player ids, which start at 1 and increment
	// per join, will reach in one server run.
	worldEntityIDBase = 1 << 20
)

// Server owns the world. All methods are safe for concurrent use.
type Server struct {
	terrain    *terrain.Field
	seed       uint64
	tickHz     uint16
	terrainF   []byte // pre-encoded terrain frame, sent on every join
	defsF      []byte // pre-encoded defs frame, built once at startup
	collidersF []byte // pre-encoded colliders frame, built once at startup
	colliders  []protocol.Collider

	reg   *defs.Registry
	world *sim.World // static NPC/target entities, composed from zones
	// worldEnts is a stable-order cache over world.Ents, never mutated after
	// New. It is server-internal only: NPC/target entities are not yet
	// broadcast as spawn/snapshot rows (a client can't render or interact
	// with them until a later task wires that up — see the report), but
	// they must still exist here so MsgFire has something for
	// sim.ResolveShot to hit.
	worldEnts []*sim.Ent
	// npcAI is the per-tick AI runner state for the combat NPCs, parallel to
	// the subset of worldEnts that are combat NPCs. Shop NPCs have no entry.
	npcAI []*npcAI
	// worldID is the next id for a runtime-spawned world entity (projectiles,
	// loot). It continues the zone-placed range so the two never collide.
	worldID uint32
	history *sim.History
	rng     *rand.Rand // ResolveShot's shared RNG; guarded by mu (fire() and tick() both hold it)

	// store is the persistence backend. It is nil until something wires a
	// *store.Store into New (out of scope here: New's signature is shared
	// with cmd/server/main.go, which does not construct one yet) — every
	// session is therefore ephemeral for now, per PROTOCOL "Identity token"
	// ("An empty or malformed token is treated as absent").
	store *store.Store

	upgrader websocket.Upgrader

	mu       sync.Mutex
	nextID   uint32
	clients  map[uint32]*client
	closing  bool
	tickNo   uint32
	list     []*client // tick-scoped, reused
	snapBuf  []byte    // tick-scoped snapshot body, reused
	snapPool sync.Pool // []byte fan-out buffers (per-client ack_seq patch)
}

// New builds a Server over the generated terrain field. It loads the content
// registry, flattens every zone into t (GDD "Static colliders" -> "The site
// is flattened, not assumed flat"), and composes each zone's colliders and
// entity placements to world space — all before t is encoded for the wire,
// so the terrain payload a client receives already agrees with the
// colliders and target/NPC positions it also receives.
// SetStore attaches the persistence backend.
//
// Kept separate from New so cmd/server owns the DSN and the migration, and so
// a nil store stays a supported mode rather than a special case: without it
// every session is ephemeral, which is exactly what an empty token already
// means (docs/PROTOCOL.md, "Identity token"). Call before serving.
func (s *Server) SetStore(st *store.Store) { s.store = st }

func New(t *terrain.Field, seed uint64) *Server {
	reg, err := defs.Load()
	if err != nil {
		// The content registry is embedded, repo-controlled data (server/data).
		// A failure here is a broken build, not a runtime condition — same
		// judgment call defs.ComposeZone's own doc comment makes for a zone
		// that will not compose. New has no error return (its signature is
		// shared with cmd/server/main.go), so this is the load-bearing check.
		panic(fmt.Errorf("server: load defs: %w", err))
	}

	// Zone iteration order must be deterministic (world entity ids are
	// assigned in this order), and map range order is not — sort zone ids.
	zoneIDs := make([]string, 0, len(reg.Zones))
	for id := range reg.Zones {
		zoneIDs = append(zoneIDs, id)
	}
	sort.Strings(zoneIDs)

	// Shared with the `dump` subcommand so the two cannot end up on different
	// planets (defs.BuildTerrain's doc comment has the detail).
	defs.ApplyZoneFlattening(t, reg)

	radiusFn := func(d [3]float64) float64 { return t.SampleRadius(terrain.Vec(d)) }

	world := sim.NewWorld()
	var worldEnts []*sim.Ent
	var allColliders []protocol.Collider
	// World entity ids are drawn from a separate range above where player
	// ids (Server.nextID, starting at 1) will ever reach in a single run, so
	// the two id spaces never collide and player ids keep starting at 1
	// regardless of how many targets/NPCs a zone places.
	worldID := uint32(worldEntityIDBase)
	for _, id := range zoneIDs {
		z := reg.Zones[id]
		cols, placements, err := defs.ComposeZone(z, radiusFn)
		if err != nil {
			panic(fmt.Errorf("server: compose zone %q: %w", id, err))
		}
		allColliders = append(allColliders, cols...)
		for _, p := range placements {
			worldID++
			kind := sim.EntityKind(protocol.EntityTypeTarget)
			var data any
			if p.Type == "npc" {
				kind = sim.EntityKind(protocol.EntityTypeNPC)
				// sim.NPCState carries the post and respawn timer the sim
				// owns; the AI runner's own state lives separately in
				// Server.npcAI, split by which package owns the rule.
				data = &sim.NPCState{
					Archetype: p.Def,
					Post:      p.Pos,
					PostQuat:  p.Quat,
					MaxHealth: reg.NPCs[p.Def].MaxHealth,
				}
			}
			ent := &sim.Ent{
				ID:     worldID,
				Kind:   kind,
				Pos:    p.Pos,
				Quat:   p.Quat,
				Health: reg.Entities[entityDefKind(kind)].MaxHealth,
				Def:    p.Def,
				Data:   data,
			}
			if kind == sim.EntityKind(protocol.EntityTypeNPC) {
				if h := reg.NPCs[p.Def].MaxHealth; h > 0 {
					ent.Health = h
				}
			}
			world.Add(ent)
			worldEnts = append(worldEnts, ent)
		}
	}

	s := &Server{
		terrain: t,
		seed:    seed,
		tickHz:  sim.TickHz,
		upgrader: websocket.Upgrader{
			CheckOrigin: func(*http.Request) bool { return true },
		},
		clients:   make(map[uint32]*client),
		reg:       reg,
		world:     world,
		worldEnts: worldEnts,
		history:   sim.NewHistory(sim.HistoryTicks),
		rng:       rand.New(rand.NewSource(int64(seed))),
		colliders: allColliders,
		worldID:   worldID,
	}
	for _, e := range worldEnts {
		if e.Kind != sim.EntityKind(protocol.EntityTypeNPC) {
			continue
		}
		if n := newNPCAI(e, reg.NPCs[e.Def]); n != nil {
			s.npcAI = append(s.npcAI, n)
		}
	}
	// The terrain frame is pre-encoded once, AFTER flattening above: it is
	// just the 2-byte type prefix over the field's canonical payload
	// (PROTOCOL "terrain"). Encoding it before flatten would ship a field
	// that disagrees with the colliders/placements just composed against it.
	s.terrainF = frame(protocol.MsgTerrain, t.Encode())
	s.defsF = protocol.EncodeDefs(protocol.Defs{Data: reg.Payload})
	s.collidersF = protocol.EncodeColliders(protocol.Colliders{List: allColliders})
	s.snapPool.New = func() any { return []byte(nil) }
	return s
}

// frame prepends the 2-byte little-endian message type to payload.
func frame(typ uint16, payload []byte) []byte {
	b := make([]byte, 2+len(payload))
	binary.LittleEndian.PutUint16(b[0:2], typ)
	copy(b[2:], payload)
	return b
}

// entityDefKind maps a world entity's Kind back to its defs.Registry.Entities
// key (the generic "player"/"npc"/"target" row, not the specific archetype
// carried in Ent.Def/Data).
func entityDefKind(k sim.EntityKind) string {
	switch uint16(k) {
	case protocol.EntityTypeNPC:
		return "npc"
	case protocol.EntityTypeTarget:
		return "target"
	default:
		return "player"
	}
}

// HandleWS upgrades /ws and runs the connection's reader and writer.
func (s *Server) HandleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return // the upgrader already answered
	}
	// Oversized frames are rejected with close code 1009 by the library
	// (PROTOCOL: max message size 64 KiB).
	conn.SetReadLimit(protocol.MaxMessageSize)
	c := &client{srv: s, conn: conn, out: make(chan msg, outQueue), done: make(chan struct{})}
	conn.SetPongHandler(c.onPong)
	conn.SetReadDeadline(time.Now().Add(silentTimeout))
	go c.writer()
	c.reader() // blocks until the connection dies
}

// Run starts the fixed-tick loop and stops it when ctx is done.
func (s *Server) Run(ctx context.Context) {
	t := time.NewTicker(time.Second / time.Duration(s.tickHz))
	defer t.Stop()
	var lastLog time.Time
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-t.C:
			s.tick()
			if now.Sub(lastLog) >= 5*time.Second {
				lastLog = now
				s.mu.Lock()
				n, tick := len(s.clients), s.tickNo
				s.mu.Unlock()
				log.Printf("tick %d: %d client(s), %d Hz", tick, n, s.tickHz)
			}
		}
	}
}

// CloseAll shuts the world down: stops admitting work, closes every
// connection, and broadcasts nothing (the server is going away).
func (s *Server) CloseAll() {
	s.mu.Lock()
	s.closing = true
	cs := make([]*client, 0, len(s.clients))
	for _, c := range s.clients {
		cs = append(cs, c)
	}
	s.clients = make(map[uint32]*client)
	s.mu.Unlock()
	for _, c := range cs {
		c.teardown()
	}
}

// tick advances the whole world by one step and fans out the snapshot.
// Step + encode happen under the world lock (both are fast and lock-free
// internally); fan-out is non-blocking channel sends.
func (s *Server) tick() {
	t0 := time.Now()
	s.mu.Lock()
	s.tickNo++
	tick := s.tickNo
	s.list = s.list[:0]
	for _, c := range s.clients {
		c.step(s.terrain, s.colliders)
		s.history.Record(tick, c.entity.ID, [3]float64(c.entity.State.Pos), [3]float64(terrain.Normalize(c.entity.State.Pos)))
		s.list = append(s.list, c)
	}
	s.stepNPCs(tick)
	s.stepPlayerVitals()

	var events []protocol.Event
	s.world.Step(sim.DT, sim.StepCtx{
		Events:    &events,
		World:     s.world,
		Terrain:   s.terrain,
		Colliders: s.colliders,
		DefOf:     s.entityDef,
	})
	for _, e := range s.worldEnts {
		s.history.Record(tick, e.ID, e.Pos, [3]float64(terrain.Normalize(terrain.Vec(e.Pos))))
	}
	// Announce anything created or destroyed this tick before encoding, so a
	// client never receives a snapshot row for an entity it has not been told
	// the type of.
	s.syncWorldEnts()

	body := s.encodeSnapshot(tick)
	// Fan out under the lock: sendSnapshot is a non-blocking channel
	// send, and the shared body must not be recycled by the next tick
	// while copies are in flight.
	for _, c := range s.list {
		c.sendSnapshot(body, uint16(c.ackSeq.Load()))
	}
	for _, ev := range events {
		f := protocol.EncodeEvent(ev)
		for _, c := range s.list {
			c.send(msg{data: f})
		}
	}
	s.mu.Unlock()
	step := time.Since(t0)
	if step > 5*time.Millisecond {
		log.Printf("slow tick %d: step %s with %d entities", tick, step, len(s.list))
	}
}

// encodeSnapshot renders the full snapshot frame once (body shared by all
// clients; ack_seq is patched per client in sendSnapshot). Must be called
// with s.mu held.
func (s *Server) encodeSnapshot(tick uint32) []byte {
	// Players first, then the world's NPCs and targets. Both go out every
	// tick: without them a client can see neither the shopkeeper it is meant
	// to buy from nor the targets it is meant to shoot, which is the whole of
	// Phase 2's playable loop.
	n := len(s.list) + len(s.worldEnts)
	size := 2 + 8 + n*protocol.EntitySize // type | tick,ack,count | rows
	if cap(s.snapBuf) < size {
		s.snapBuf = make([]byte, 0, size)
	}
	b := s.snapBuf[:0]
	// AppendSnapshotHeader emits the type prefix + tick/ack/count.
	b = protocol.AppendSnapshotHeader(b, tick, 0, uint16(n))
	for _, c := range s.list {
		q := c.entity.State.OrientationQuat()
		p, v := c.entity.State.Pos, c.entity.State.Vel
		b = protocol.AppendEntity(b, protocol.Entity{
			ID:     c.entity.ID,
			Pos:    v3f32(p),
			Quat:   v4f32([4]float64(q)),
			Vel:    v3f32(v),
			Health: healthU16(c.entity.Health),
			Flags:  c.flags(tick),
			PitchQ: c.pitchQ(),
		})
	}
	for _, e := range s.worldEnts {
		b = protocol.AppendEntity(b, protocol.Entity{
			ID:     e.ID,
			Pos:    v3f32(e.Pos),
			Quat:   v4f32(e.Quat),
			Vel:    v3f32(e.Vel),
			Health: healthU16(e.Health),
			Flags:  e.Flags,
			PitchQ: e.PitchQ,
		})
	}
	return b
}

func healthU16(h int) uint16 {
	if h < 0 {
		return 0
	}
	if h > math.MaxUint16 {
		return math.MaxUint16
	}
	return uint16(h)
}

func v3f32(v [3]float64) [3]float32 {
	return [3]float32{float32(v[0]), float32(v[1]), float32(v[2])}
}

func v4f32(v [4]float64) [4]float32 {
	return [4]float32{float32(v[0]), float32(v[1]), float32(v[2]), float32(v[3])}
}

// join registers c as a new player: allocates the entity and identity,
// enqueues the join handshake (hello_ack, terrain, defs, colliders, spawn
// for every existing entity, spawn for self), then publishes c to the world
// so the next tick includes it. The handshake is fully enqueued before
// publication, so the client's stream order is hello_ack, terrain, defs,
// colliders, spawn(s), then snapshots (FIFO per connection; PROTOCOL.md
// "terrain"/"defs"/"colliders").
func (s *Server) join(c *client, h protocol.Hello) {
	if h.ClientVer != protocol.VersionPhase2 {
		c.fail() // wrong protocol version (PROTOCOL.md "Versioning"): close 1002
		return
	}
	s.mu.Lock()
	if c.entity != nil || s.closing {
		s.mu.Unlock()
		c.fail() // already joined or server shutting down
		return
	}
	s.nextID++
	id := s.nextID
	c.id = id
	name := SanitizeName(h.Name, id)
	spawnState := sim.SpawnState(s.terrain)
	c.entity = &entity{
		ID:     id,
		Name:   name,
		State:  spawnState,
		Health: s.reg.Entities["player"].MaxHealth,
	}
	c.entity.PrevLook = c.entity.State.Facing
	// Vitals must start at full health. Zero-valued Vitals means Health 0, and
	// stepPlayerVitals copies that onto the entity every tick — so a player
	// would join already dead, unable to fire, with nothing on screen saying
	// why. Seed it from the same def the entity's health came from.
	c.vitals = sim.Vitals{Health: c.entity.Health}
	c.rate = newCmdRate(time.Now())
	token := h.Token
	if s.store == nil {
		token = "" // no store attached: sessions are ephemeral by design (SetStore)
	}
	c.ident = joinIdentity(context.Background(), s.store, s.reg, token, name, [3]float64(spawnState.Pos))

	// Collect the world's entities and the existing players' spawn rows before
	// publishing self. World entities exist from server start and never
	// despawn, so a joining client learns them exactly once, here — the same
	// contract PROTOCOL states for any entity already in the world.
	others := make([]msg, 0, len(s.clients)+len(s.worldEnts))
	for _, e := range s.worldEnts {
		others = append(others, msg{data: protocol.EncodeSpawn(protocol.Spawn{
			EntityID:   e.ID,
			EntityType: uint16(e.Kind),
			Data:       []byte(e.Def),
		})})
	}
	for _, oc := range s.clients {
		others = append(others, msg{data: protocol.EncodeSpawn(protocol.Spawn{
			EntityID:   oc.entity.ID,
			EntityType: protocol.EntityTypePlayer,
			Data:       []byte(oc.entity.Name),
		})})
	}
	s.mu.Unlock()

	self := protocol.EncodeSpawn(protocol.Spawn{
		EntityID:   id,
		EntityType: protocol.EntityTypePlayer,
		Data:       []byte(name),
	})
	c.send(msg{data: protocol.EncodeHelloAck(protocol.HelloAck{
		ServerVer: protocol.VersionPhase2,
		TickHz:    s.tickHz,
		WorldSeed: uint32(s.seed),
		EntityID:  id,
	})})
	c.send(msg{data: s.terrainF})
	c.send(msg{data: s.defsF})
	c.send(msg{data: s.collidersF})
	for _, m := range others {
		c.send(m)
	}
	c.send(msg{data: self})

	// Publish, then tell the existing clients a body appeared.
	s.mu.Lock()
	s.clients[id] = c
	for _, oc := range s.clients {
		if oc != c {
			oc.send(msg{data: self})
		}
	}
	s.mu.Unlock()

	// After publishing, so every client that must hear the joiner's weapon
	// is already in s.clients.
	s.syncEquipped(c)
}

// leave removes c's entity from the world and tells the remaining clients.
func (s *Server) leave(c *client) {
	s.mu.Lock()
	if c.entity == nil {
		s.mu.Unlock()
		return
	}
	id := c.entity.ID
	delete(s.clients, id)
	s.history.Forget(id)
	s.mu.Unlock()

	if c.ident != nil {
		c.ident.Close(context.Background())
	}

	f := protocol.EncodeDespawn(protocol.Despawn{EntityID: id})
	s.mu.Lock()
	for _, oc := range s.clients {
		oc.send(msg{data: f})
	}
	s.mu.Unlock()
}

// doCmd builds the requester's server-truth cmdWorld and runs their cmd
// through handleCmd (docs/tasks/phase2-wave2.md "W2-14"). It snapshots the
// identity's player row, hands handleCmd a pointer to the copy (safe: the
// copy is only ever touched by this connection's own reader goroutine), and
// writes any mutation back — the same read/mutate/write-back shape
// identity.go's Mutate doc comment calls for.
func (s *Server) doCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	var result protocol.CmdResult
	var before, after string
	c.ident.Mutate(func(p *store.Player) {
		before = p.Equipped[slotPrimary]
		result = handleCmd(c.rate, time.Now(), req, cmdWorld{
			Player:  p,
			Reg:     s.reg,
			Pos:     c.entity.State.Pos,
			Up:      terrain.Normalize(c.entity.State.Pos),
			Look:    c.lookDir(),
			FindNPC: s.findNPC,
		})
		after = p.Equipped[slotPrimary]
	})
	// The primary slot is on no entity row, so a change reaches the other
	// clients only as an `equipped` event (PROTOCOL.md event_id 0x0006).
	// Broadcast outside Mutate: the cmd path takes the identity lock and then
	// s.mu (handleCmd -> findNPC), so taking them the other way round here
	// would invert the order.
	if after != before {
		f := equippedFrame(c.entity.ID, after)
		s.mu.Lock()
		s.broadcast(f)
		s.mu.Unlock()
	}
	return result
}

// slotPrimary is the equipment slot the `equipped` event reports. Only the
// slot that is visible in another player's hands goes on the wire.
const slotPrimary = "primary"

// equippedFrame renders the `equipped` event for a player's primary slot:
// entity_id is the player, data is the item id as UTF-8, empty for "nothing
// equipped" (PROTOCOL.md event_id 0x0006).
func equippedFrame(entityID uint32, item string) []byte {
	return protocol.EncodeEvent(protocol.Event{
		EntityID: entityID,
		EventID:  protocol.EventEquipped,
		Data:     []byte(item),
	})
}

// syncEquipped brings a joining client and the clients already in the world
// into agreement about who is holding what: one `equipped` event per armed
// player replayed to the joiner, and the joiner's own weapon announced to
// everyone else (a reconnecting player arrives already armed, off their
// stored row).
//
// The lock dance is deliberate. Reading an identity takes its mutex, and the
// cmd path already takes that mutex before s.mu, so s.mu is released before
// any Snapshot call and retaken to send.
func (s *Server) syncEquipped(c *client) {
	s.mu.Lock()
	peers := make([]*client, 0, len(s.clients))
	for _, oc := range s.clients {
		if oc != c {
			peers = append(peers, oc)
		}
	}
	s.mu.Unlock()

	frames := make([][]byte, 0, len(peers))
	for _, oc := range peers {
		if item := oc.ident.Snapshot().Equipped[slotPrimary]; item != "" {
			frames = append(frames, equippedFrame(oc.entity.ID, item))
		}
	}
	self := c.ident.Snapshot().Equipped[slotPrimary]

	for _, f := range frames {
		c.send(msg{data: f})
	}
	if self == "" {
		return
	}
	f := equippedFrame(c.entity.ID, self)
	s.mu.Lock()
	for _, oc := range peers {
		oc.send(msg{data: f})
	}
	s.mu.Unlock()
}

// findNPC looks up a shop NPC's archetype and world position by entity id,
// for cmd.go's inRange/shop rules — cmdWorld.FindNPC.
func (s *Server) findNPC(entityID uint32) (defs.NPC, sim.Vec, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.world.Ents[entityID]
	if e == nil || e.Kind != sim.EntityKind(protocol.EntityTypeNPC) {
		return defs.NPC{}, sim.Vec{}, false
	}
	// Ent.Def is the archetype id and is set for every placement. Data used to
	// hold it as a bare string, but NPCs now carry *sim.NPCState there, and a
	// type assertion for the old shape fails silently — shop_list then answers
	// StatusNotFound with an empty body, which reads as "the shop is broken"
	// rather than "the lookup changed shape".
	npc, ok := s.reg.NPCs[e.Def]
	if !ok {
		return defs.NPC{}, sim.Vec{}, false
	}
	return npc, sim.Vec(e.Pos), true
}

// weaponFor resolves id (a player's equipped primary item) to its weapon
// rule table. false when id names no item, or an item with no weapon table.
func (s *Server) weaponFor(id string) (defs.Weapon, bool) {
	if id == "" {
		return defs.Weapon{}, false
	}
	item, ok := s.reg.Items[id]
	if !ok || item.Weapon == nil {
		return defs.Weapon{}, false
	}
	return *item.Weapon, true
}

// fire resolves one MsgFire request (docs/PROTOCOL.md "fire — shooting"):
// cadence and ammunition are enforced server-side against the shooter's
// equipped weapon and this connection's ephemeral magazine; the rewind is
// bounded by the server's own smoothed RTT/2 (never a client value); a
// resolved shot broadcasts shot_fired always, hit when it lands (death
// follows a tick later from sim.StepTarget's own health check, reused
// rather than duplicated here).
func (s *Server) fire(c *client, f protocol.Fire) {
	primary := c.ident.Snapshot().Equipped[slotPrimary]
	wp, ok := s.weaponFor(primary)
	if !ok {
		return // nothing equipped, or an unknown item: drop the shot
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	tick := s.tickNo
	intervalTicks := uint32(math.Round(wp.FireInterval * sim.TickHz))
	tolerance := uint32(1)
	if c.entity.LastFireTick != 0 {
		minTick := c.entity.LastFireTick + intervalTicks
		if minTick > tolerance {
			minTick -= tolerance
		} else {
			minTick = 0
		}
		if tick < minTick {
			return // too soon: dropped, not queued
		}
	}

	if primary != c.entity.EquippedWeapon {
		c.entity.EquippedWeapon = primary
		c.entity.Magazine = wp.Magazine
	}
	if c.entity.Magazine <= 0 {
		return // empty magazine: dropped
	}

	rewindTicks := c.rewindTicks()
	rewindTick := tick - uint32(rewindTicks)
	// Early-out before spending a round: ResolveShot also needs this sample,
	// but a shot it cannot resolve should not cost the player ammunition.
	// The origin itself comes back on the returned Ray — computing it here as
	// well would be two copies of one rule, free to drift apart.
	if _, _, ok := s.history.At(rewindTick, c.entity.ID); !ok {
		return // no history for this shooter at the rewound tick yet
	}
	dir := sim.Vec{float64(f.Dir[0]), float64(f.Dir[1]), float64(f.Dir[2])}

	c.entity.Magazine--
	c.entity.LastFireTick = tick
	c.entity.FiringTick = tick

	shot := sim.Shot{
		Shooter:       c.entity.ID,
		Dir:           [3]float64(dir),
		Tick:          tick,
		RewindTicks:   rewindTicks,
		ConeHalfAngle: wp.SpreadBase * math.Pi / 180,
	}
	ray, hit, found := sim.ResolveShot(s.world, s.history, shot, wp, s.entityDef, s.rng)

	// Broadcast the ray the SERVER resolved — its rewound origin and its
	// post-spread direction — not the client's aim. The server owns spread, so
	// the client's direction is not where the shot went; drawing it would put
	// every player's tracer along a line that disagrees with the hit markers.
	rayOrigin := sim.Vec(ray.Origin)
	dist := wp.MaxRange
	if found {
		dist = rayOrigin.Sub(hit.Point).Len()
	}
	shotData := make([]byte, 0, 28)
	for i := 0; i < 3; i++ {
		shotData = appendF32(shotData, float32(ray.Origin[i]))
	}
	for i := 0; i < 3; i++ {
		shotData = appendF32(shotData, float32(ray.Dir[i]))
	}
	shotData = appendF32(shotData, float32(dist))
	shotFrame := protocol.EncodeEvent(protocol.Event{
		EntityID: c.entity.ID,
		EventID:  protocol.EventShotFired,
		Data:     shotData,
	})
	for _, oc := range s.clients {
		oc.send(msg{data: shotFrame})
	}

	if found {
		hitData := make([]byte, 0, 20)
		hitData = binary.LittleEndian.AppendUint32(hitData, c.entity.ID)
		for i := 0; i < 3; i++ {
			hitData = appendF32(hitData, float32(hit.Point[i]))
		}
		hitData = binary.LittleEndian.AppendUint16(hitData, uint16(hit.Damage))
		hitData = binary.LittleEndian.AppendUint16(hitData, uint16(hit.HealthAfter))
		hitFrame := protocol.EncodeEvent(protocol.Event{
			EntityID: hit.Victim,
			EventID:  protocol.EventHit,
			Data:     hitData,
		})
		for _, oc := range s.clients {
			oc.send(msg{data: hitFrame})
		}
	}
}

// entityDef is ResolveShot's defOf callback.
func (s *Server) entityDef(e *sim.Ent) defs.EntityDef {
	return s.reg.Entities[entityDefKind(e.Kind)]
}

func appendU32(b []byte, v uint32) []byte { return binary.LittleEndian.AppendUint32(b, v) }

func appendU16(b []byte, v uint16) []byte { return binary.LittleEndian.AppendUint16(b, v) }

func appendF32(b []byte, v float32) []byte {
	return binary.LittleEndian.AppendUint32(b, math.Float32bits(v))
}
