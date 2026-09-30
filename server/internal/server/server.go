// Package server is the authoritative game server: a WebSocket gateway on
// /ws (PROTOCOL.md) feeding a fixed-tick (20 Hz) simulation. The world is an
// in-memory map of entities, one per connected player in M1; the tick loop
// steps every entity against the shared terrain field and fans out one full
// snapshot per tick, patching the per-client ack_seq into a shared body.
package server

import (
	"context"
	"encoding/binary"
	"encoding/json"
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
	// 256, up from 32: send() now KILLS a client whose queue overflows
	// (client.go send — the poison-client wedge), so the buffer must absorb
	// any legitimate burst. The join replay alone is ~30 frames back to
	// back; 256 small frames is cheap insurance against killing a healthy
	// client whose writer is one syscall behind.
	outQueue = 256
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
	// Events raised outside world.Step (the NPC loot roll runs in stepNPCs,
	// before the step allocates its event list) queue here and join the
	// tick's stream. Without this, DropLoot's LootDropped event went to a
	// nil ctx.Events and no client ever saw one.
	pendingEvents []protocol.Event
	// Phase 10 bounty machine (bounty.go), guarded by mu.
	bounty         *bountyState
	bountyRepostAt time.Time
	bountyRotation int
	propsF         []byte // pre-encoded props frame, built once at startup
	colliders      []protocol.Collider

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
	// pendingSpills are deaths whose material spill waits for the identity
	// (gather.go); guarded by mu, drained by tick() after it unlocks.
	pendingSpills []spill

	// store is the persistence backend. It is nil until something wires a
	// *store.Store into New (out of scope here: New's signature is shared
	// with cmd/server/main.go, which does not construct one yet) — every
	// session is therefore ephemeral for now, per PROTOCOL "Identity token"
	// ("An empty or malformed token is treated as absent").
	store *store.Store

	upgrader websocket.Upgrader
	gate     *gatekeeper

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

func New(t *terrain.Field, seed uint64) (*Server, error) {
	reg, err := defs.Load()
	if err != nil {
		// The content registry is embedded, repo-controlled data (server/data).
		// A failure here is a broken build, not a runtime condition — but a
		// content typo should die with a message at startup, not a stack
		// trace, so it surfaces as an error rather than a panic.
		return nil, fmt.Errorf("load defs: %w", err)
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
	var allProps []protocol.Prop
	// World entity ids are drawn from a separate range above where player
	// ids (Server.nextID, starting at 1) will ever reach in a single run, so
	// the two id spaces never collide and player ids keep starting at 1
	// regardless of how many targets/NPCs a zone places.
	worldID := uint32(worldEntityIDBase)
	for _, id := range zoneIDs {
		z := reg.Zones[id]
		cols, placements, props, err := defs.ComposeZone(z, radiusFn)
		if err != nil {
			return nil, fmt.Errorf("compose zone %q: %w", id, err)
		}
		allColliders = append(allColliders, cols...)
		allProps = append(allProps, props...)
		for _, p := range placements {
			worldID++
			kind := sim.EntityKind(protocol.EntityTypeTarget)
			var data any
			if p.Type == "npc" {
				kind = sim.EntityKind(protocol.EntityTypeNPC)
				// sim.NPCState carries the post and respawn timer the sim
				// owns; the AI runner's own state lives separately in
				// Server.npcAI, split by which package owns the rule.
				//
				// Whether a placement gets that state at all is one rule, and
				// it lives in sim.CombatStateFor — this loop is a second
				// implementation of sim.SpawnZoneNPCs, and when they disagreed
				// it was this one that ran (see CombatStateFor). Checking for
				// nil rather than assigning straight through is required: a
				// typed nil in an interface is not a nil interface.
				if st := sim.CombatStateFor(p.Def, reg.NPCs[p.Def], p.Pos, p.Quat); st != nil {
					data = st
				}
			}
			if p.Type == "node" {
				// Phase 12: a node's health pool is its yields (defs audit
				// guarantees the def exists).
				kind = sim.EntityKind(protocol.EntityTypeNode)
				nd := reg.Nodes[p.Def]
				data = sim.NewNodeState(nd.Yields, nd.Respawn)
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
			if kind == sim.EntityKind(protocol.EntityTypeNode) {
				ent.Health = reg.Nodes[p.Def].Yields
			}
			world.Add(ent)
			worldEnts = append(worldEnts, ent)
		}
	}

	// Phase 4: one rover, parked deterministically near spawn (GDD "Ground
	// drive model", "Deterministic spawn"). A world entity like any other —
	// its step is registered by Kind, its def carries the asset.
	worldID++
	roverPos, roverQuat := sim.SpawnRover(t)
	rover := &sim.Ent{
		ID:     worldID,
		Kind:   sim.EntityKind(protocol.EntityTypeVehicle),
		Pos:    [3]float64(roverPos),
		Quat:   [4]float64(roverQuat),
		Health: reg.Entities["vehicle"].MaxHealth,
		Def:    "vehicle",
		Data:   sim.NewVehicleState(),
	}
	world.Add(rover)
	worldEnts = append(worldEnts, rover)

	s := &Server{
		terrain: t,
		seed:    seed,
		tickHz:  sim.TickHz,
		upgrader: websocket.Upgrader{
			// Phase 6: no Origin (native clients) passes; a browser origin
			// must be on SA_ALLOWED_ORIGINS (gatekeeper.go). The identity
			// token stays an unverified bearer per the Deferred table —
			// its trigger is "players other than us", not "a LAN exists".
			CheckOrigin: checkOrigin,
		},
		gate:      newGatekeeper(),
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
	// Zone dressing. Visual only and pre-encoded once, exactly like the
	// colliders it sits among -- a client that never decodes this still agrees
	// with the server about everything that can be walked into or shot.
	if len(allProps) > protocol.PropMax {
		return nil, fmt.Errorf("%d props exceeds PropMax %d", len(allProps), protocol.PropMax)
	}
	s.propsF = protocol.EncodeProps(protocol.Props{List: allProps})
	s.snapPool.New = func() any { return []byte(nil) }
	return s, nil
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
	case protocol.EntityTypeVehicle:
		return "vehicle"
	case protocol.EntityTypeShip:
		return "ship"
	case protocol.EntityTypeNode:
		return "node"
	default:
		return "player"
	}
}

// HandleWS upgrades /ws and runs the connection's reader and writer.
func (s *Server) HandleWS(w http.ResponseWriter, r *http.Request) {
	// LAN exposure gate (Phase 6, C52): per-IP cap and join rate, refused
	// before the upgrade so a flood costs one HTTP response per attempt.
	// HandleWS blocks for the connection's whole life, so the deferred
	// release fires exactly when the socket dies.
	ip := clientIP(r)
	if !s.gate.admit(ip, time.Now()) {
		http.Error(w, "too many connections from this address", http.StatusTooManyRequests)
		return
	}
	defer s.gate.release(ip)

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

// Registry exposes the content registry for the account site's player
// minting (Phase 7) — read-only after New.
func (s *Server) Registry() *defs.Registry { return s.reg }

// OnlineCount is the landing page's live-connection statistic (Phase 7).
func (s *Server) OnlineCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.clients)
}

// Run starts the fixed-tick loop and stops it when ctx is done.
func (s *Server) Run(ctx context.Context) {
	t := time.NewTicker(time.Second / time.Duration(s.tickHz))
	defer t.Stop()
	// The scout sweep walks positions against POI discovery radii once a
	// second — its own cadence, its own goroutine, no identity lock under
	// s.mu (missions.go, "lock order").
	scout := time.NewTicker(time.Second)
	defer scout.Stop()
	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case <-scout.C:
				s.scoutSweep()
				s.bountyTick()
				s.skillsTick()
			}
		}
	}()
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
	// Vehicle inputs are zero unless a seated driver writes them below —
	// which is the whole of driver-disconnect handling: no driver, zero
	// input, the same step coasts the rover to a stop (GDD "Control
	// handoff").
	for _, e := range s.worldEnts {
		switch v := e.Data.(type) {
		case *sim.VehicleState:
			v.Throttle, v.Steer = 0, 0
		case *sim.ShipState:
			v.Thrust, v.Roll, v.YawRate, v.PitchRate, v.Boost = 0, 0, 0, 0, false
		}
	}
	for _, c := range s.clients {
		switch {
		case c.seat == 0:
			c.step(s.terrain, s.colliders)
			s.history.Record(tick, c.entity.ID, [3]float64(c.entity.State.Pos), [3]float64(terrain.Normalize(c.entity.State.Pos)))
		case c.seat == 1:
			// The control seat's input drives the vehicle; the body itself
			// is attached, not co-simulated (GDD "Seats and occupancy").
			// Mode is a declaration, not authority: occupancy AND the
			// vehicle's kind decide the interpretation (PROTOCOL "input") —
			// a rover driver's v[0..1] are throttle/steer, a ship pilot's
			// v[0..3] are thrust/roll/yaw_rate/pitch_rate plus the boost
			// bit.
			if ent, _ := s.vehicleByID(c.seatVehicle); ent != nil {
				if w := c.input.Load(); w != nil {
					switch v := ent.Data.(type) {
					case *sim.VehicleState:
						v.Throttle, v.Steer = float64(w.MoveX), float64(w.MoveY)
						v.EffMult = c.driveMult
					case *sim.ShipState:
						v.Thrust = float64(w.MoveX)
						v.Roll = float64(w.MoveY)
						v.YawRate = float64(w.LookDir[0])
						v.PitchRate = float64(w.LookDir[1])
						v.Boost = w.ActionMask&protocol.ActionBoost != 0
						v.EffMult = c.flightMult
					}
				}
			}
		}
		c.recordCmdTick(tick)
		s.list = append(s.list, c)
	}
	s.stepNPCs(tick)
	s.stepPlayerVitals()
	yields := s.stepGathers()
	spills := s.pendingSpills
	s.pendingSpills = nil

	events := s.pendingEvents
	s.pendingEvents = nil
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
	// Phase 11: metre accumulators for the skill sweep. Deltas come off the
	// same post-step state the snapshot ships, classified by how the metres
	// were earned: sprinting on foot, driving seat 1 of a rover, piloting a
	// ship (which also owns the clean-landing bonus).
	for _, c := range s.list {
		pos := [3]float64(c.entity.State.Pos)
		if c.seat != 0 {
			if ent, _ := s.vehicleByID(c.seatVehicle); ent != nil {
				pos = ent.Pos
			}
		}
		if c.hasLastPos {
			d := sim.Vec{pos[0] - c.lastPos[0], pos[1] - c.lastPos[1], pos[2] - c.lastPos[2]}.Len()
			if d < 5 { // a respawn teleport is not training
				switch {
				case c.seat == 0:
					if w := c.input.Load(); w != nil && w.ActionMask&protocol.ActionSprint != 0 && c.entity.State.Grounded {
						c.sprintMeters += d
					}
				case c.seat == 1:
					if ent, _ := s.vehicleByID(c.seatVehicle); ent != nil {
						switch ent.Kind {
						case sim.EntityKind(protocol.EntityTypeVehicle):
							c.driveMeters += d
						case sim.EntityKind(protocol.EntityTypeShip):
							c.flyMeters += d
							grounded := ent.Flags&protocol.FlagGrounded != 0
							now := float64(tick) / float64(sim.TickHz)
							if !grounded && c.airborneAt == 0 {
								c.airborneAt = now
							}
							if grounded && c.airborneAt > 0 {
								if now-c.airborneAt >= 3 {
									c.awardLocked("piloting", s.reg.Awards.LandingXP)
								}
								c.airborneAt = 0
							}
						}
					}
				}
			}
		}
		c.lastPos = pos
		c.hasLastPos = true
	}

	// Seated bodies compose from the POST-step vehicle transform, so the
	// snapshot's seat position and the vehicle it rides never disagree by a
	// tick. Their lag-comp history records the composed position.
	for _, c := range s.list {
		if c.seat == 0 {
			continue
		}
		if ent, bank := s.vehicleByID(c.seatVehicle); bank != nil {
			c.entity.State.Pos = sim.ComposeSeat(ent.Kind, sim.Vec(ent.Pos), sim.Quat(ent.Quat), c.seat)
			c.entity.State.Vel = sim.Vec(ent.Vel)
			c.entity.State.Grounded = ent.Flags&protocol.FlagGrounded != 0
		}
		s.history.Record(tick, c.entity.ID, [3]float64(c.entity.State.Pos), [3]float64(terrain.Normalize(c.entity.State.Pos)))
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
	s.drainYields(yields)
	s.drainSpills(spills)
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
		if c.seat != 0 {
			// A seated body's quat is the vehicle's (GDD composition rule);
			// pos/vel were composed after the world stepped.
			if ent, _ := s.vehicleByID(c.seatVehicle); ent != nil {
				q = sim.Quat(ent.Quat)
			}
		}
		p, v := c.entity.State.Pos, c.entity.State.Vel
		b = protocol.AppendEntity(b, protocol.Entity{
			ID:       c.entity.ID,
			Pos:      v3f32(p),
			Quat:     v4f32([4]float64(q)),
			Vel:      v3f32(v),
			ParentID: c.seatVehicle,
			Seat:     c.seat,
			Health:   healthU16(c.entity.Health),
			Flags:    c.flags(tick),
			PitchQ:   c.pitchQ(),
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
	c.send(msg{data: s.propsF})
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
	s.refreshScoutCache(c) // a reconnect arrives with its missions already live
	s.loadSkillCaches(c)   // and with its discoveries already made
	// A live unclaimed bounty replays its offer to the joiner — the
	// broadcast happened once, possibly before this client existed.
	s.mu.Lock()
	if b := s.bounty; b != nil && !b.claimed {
		c.send(msg{data: protocol.EncodeEvent(protocol.Event{
			EntityID: b.entityID,
			EventID:  protocol.EventPriorityOffer,
			Data: encodeJSON(map[string]any{
				"id": b.mission.ID, "poi": b.poi,
				"expires_s": b.mission.ClaimMinutes * 60,
			}),
		})})
	}
	s.mu.Unlock()

	// An owner's ship follows them across reconnects (GDD "Ownership":
	// spawn on join). After publishing, so the spawn broadcast lands on a
	// registered client.
	s.syncOwnedShip(c)
}

// leave removes c's entity from the world and tells the remaining clients.
func (s *Server) leave(c *client) {
	s.mu.Lock()
	if c.entity == nil {
		s.mu.Unlock()
		return
	}
	id := c.entity.ID
	s.freeSeat(c)
	s.removeFromParty(c) // roster updates reach the survivors (GDD, Phase 10)
	delete(s.clients, id)
	s.history.Forget(id)
	s.mu.Unlock()

	// The bounty claim survives a member dropping only while ANY claimant
	// remains online; the last disconnect releases it (GDD). Outside s.mu —
	// it walks the claim under its own locking.
	s.bountyClientGone(c)

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
	// Party ops read and write OTHER clients, which cmdWorld's one-player
	// view cannot; they take s.mu themselves and never touch the identity.
	switch req.Opcode {
	case protocol.OpPartyInvite, protocol.OpPartyRespond, protocol.OpPartyLeave:
		return s.partyCmd(c, req)
	case protocol.OpMissionList, protocol.OpMissionAccept,
		protocol.OpMissionAbandon, protocol.OpMissionTurnin,
		protocol.OpMissionShare:
		return s.missionCmd(c, req)
	case protocol.OpSkills:
		return s.skillsCmd(c, req)
	}
	var result protocol.CmdResult
	var before, after string
	var creditsBefore, creditsAfter int64
	c.ident.Mutate(func(p *store.Player) {
		creditsBefore = p.Credits
		before = p.Equipped[slotPrimary]
		result = handleCmd(c.rate, time.Now(), req, cmdWorld{
			Player:   p,
			Reg:      s.reg,
			Pos:      c.entity.State.Pos,
			Up:       terrain.Normalize(c.entity.State.Pos),
			Look:     c.lookDir(),
			FindNPC:  s.findNPC,
			FindNode: s.findNode,
			Ent:      c.entity,
			Busy: func() bool {
				s.mu.Lock()
				defer s.mu.Unlock()
				return c.gather.node != 0
			},
			Gather: func(node uint32, ticks int) bool {
				s.mu.Lock()
				defer s.mu.Unlock()
				return s.startGather(c, node, ticks)
			},
			CancelGather: func() bool {
				s.mu.Lock()
				defer s.mu.Unlock()
				return s.cancelGather(c, "cancel")
			},
			Rand: func() float64 {
				s.mu.Lock()
				defer s.mu.Unlock()
				return s.rng.Float64()
			},
			Buyback: func() []buybackEntry {
				s.mu.Lock()
				defer s.mu.Unlock()
				return append([]buybackEntry(nil), c.buyback...)
			},
			PushBuyback: func(e buybackEntry) {
				s.mu.Lock()
				defer s.mu.Unlock()
				c.pushBuyback(e)
			},
			PopBuyback: func(item string) (buybackEntry, bool) {
				s.mu.Lock()
				defer s.mu.Unlock()
				return c.popBuyback(item)
			},
			Vitals: func() (int, bool) {
				s.mu.Lock()
				defer s.mu.Unlock()
				return c.vitals.Health, c.vitals.DeadTicks > 0
			},
			Heal: func(n int) int {
				s.mu.Lock()
				defer s.mu.Unlock()
				c.vitals.Health += n
				if c.vitals.Health > sim.PlayerMaxHealth {
					c.vitals.Health = sim.PlayerMaxHealth
				}
				c.entity.Health = c.vitals.Health
				return c.vitals.Health
			},
			CoolingFor: func(item string) float64 {
				s.mu.Lock()
				defer s.mu.Unlock()
				return c.coolingFor(item, time.Now())
			},
			StartCooldown: func(item string, secs float64) {
				s.mu.Lock()
				defer s.mu.Unlock()
				if c.cooldowns == nil {
					c.cooldowns = map[string]time.Time{}
				}
				c.cooldowns[item] = time.Now().Add(time.Duration(secs * float64(time.Second)))
			},
			Scan: func(rng float64) []map[string]any {
				s.mu.Lock()
				defer s.mu.Unlock()
				return s.scanLocked(c, rng)
			},
		})
		after = p.Equipped[slotPrimary]
		creditsAfter = p.Credits
	})
	// Commerce trains on credits MOVED at a shop (Phase 11) — buys today,
	// sells when selling exists.
	if result.Status == protocol.StatusOK && creditsBefore != creditsAfter &&
		(req.Opcode == protocol.OpShopBuy || req.Opcode == protocol.OpShopSell || req.Opcode == protocol.OpShopBuyback) {
		if per := s.reg.Awards.CommerceXPPer5cr; per > 0 {
			moved := creditsAfter - creditsBefore
			if moved < 0 {
				moved = -moved
			}
			s.mu.Lock()
			c.awardLocked("commerce", moved/5*per)
			s.mu.Unlock()
		}
	}
	// Engineering trains per craft (Phase 12): the recipe's xp × qty.
	if req.Opcode == protocol.OpCraft && result.Status == protocol.StatusOK {
		var body struct {
			NPC    uint32 `json:"npc"`
			Recipe string `json:"recipe"`
			Qty    int    `json:"qty"`
		}
		if json.Unmarshal(req.Data, &body) == nil {
			if r, ok := s.reg.Recipes[body.Recipe]; ok && r.XP > 0 {
				s.mu.Lock()
				c.awardLocked("engineering", r.XP*int64(body.Qty))
				s.mu.Unlock()
			}
		}
	}
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
	// A successful buy may have put a ship in the inventory; make the world
	// agree (GDD "Ownership": spawn on purchase). Idempotent.
	if req.Opcode == protocol.OpShopBuy && result.Status == protocol.StatusOK {
		s.syncOwnedShip(c)
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

	// To the joiner as well as to the peers. A player whose stored row
	// already holds a weapon is told about everyone else's and nothing about
	// their own, so they reconnect with empty hands and no way to find out —
	// re-equipping the same item changes nothing, so it broadcasts nothing
	// either. Every client learns about every armed player, including itself.
	c.send(msg{data: f})

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

// findNode looks up a resource node's def, world position and remaining
// yields by entity id — cmdWorld.FindNode (Phase 12).
func (s *Server) findNode(entityID uint32) (defs.Node, sim.Vec, int, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	e := s.world.Ents[entityID]
	if e == nil || e.Kind != sim.EntityKind(protocol.EntityTypeNode) {
		return defs.Node{}, sim.Vec{}, 0, false
	}
	nd, ok := s.reg.Nodes[e.Def]
	if !ok {
		return defs.Node{}, sim.Vec{}, 0, false
	}
	return nd, sim.Vec(e.Pos), e.Health, true
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
	members, victimArch, victimID, killed := s.fireLocked(c, f)
	if killed {
		// Identity locks are only safe with s.mu released (doCmd's order);
		// the attribution was captured under the lock, the pay happens here.
		s.missionKillCredit(members, victimArch)
		s.bountyResolveKill(c, victimID)
	}
}

// fireLocked is fire's original body: everything that needs s.mu. It reports
// a kill's attribution so the mission credit can run after release.
func (s *Server) fireLocked(c *client, f protocol.Fire) (members []*client, victimArch string, victimID uint32, killed bool) {
	snap := c.ident.Snapshot()
	primary := snap.Equipped[slotPrimary]
	wp, ok := sim.WeaponWith(s.reg, &snap)
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

	rewindTicks := c.rewindTicks(tick, f.Seq)
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
		DamageMult:    c.damageMult,
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
		c.awardLocked("marksmanship", int64(hit.Damage)*s.reg.Awards.DamageXPPerPoint)
		if hit.HealthAfter == 0 {
			killed = true
			victimID = hit.Victim
			victimArch = s.npcArchetypeOf(hit.Victim)
			if n := s.npcOf(hit.Victim); n != nil {
				n.lootExtra = c.lootExtra
				if s.inDiscoveredPOI(c, n.ent.Pos) {
					n.lootExtra = c.lootExtraPOI
				}
			}
			members = append([]*client{}, s.partyMembers(c)...)
			kxp := s.reg.Awards.KillXP
			if victimArch == "npc.warlord" {
				kxp = s.reg.Awards.KillXPWarlord
			}
			c.awardLocked("marksmanship", kxp)
		}
	}
	return members, victimArch, victimID, killed
}

// npcArchetypeOf maps a victim entity id to its archetype id ("npc.grunt"),
// or "" for anything that is not an AI-run NPC. Caller holds s.mu.
func (s *Server) npcArchetypeOf(id uint32) string {
	if n := s.npcOf(id); n != nil {
		return n.arch.ID
	}
	return ""
}

// npcOf finds the AI record behind a world entity id, nil for non-NPCs.
func (s *Server) npcOf(id uint32) *npcAI {
	for _, n := range s.npcAI {
		if n.ent.ID == id {
			return n
		}
	}
	return nil
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
