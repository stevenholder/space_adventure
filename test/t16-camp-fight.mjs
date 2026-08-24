#!/usr/bin/env node
/**
 * Phase 3 acceptance: does the camp actually fight?
 *
 * Walk to the encampment, confirm the NPCs notice you (C19 aggro), that they
 * damage you (C20/C21), and that health and events reach the client. Every
 * unit test covers one rule; this is the only one that walks a player into a
 * camp, which is the thing Phase 3 claims to deliver.
 *
 * STATUS: the fight assertions do not run yet, because the walk to the camp
 * does not arrive. That is NOT a movement bug — measured headlessly, a player
 * sprinting from spawn toward the camp meets a 68.8 degree scarp at ~45 m, and
 * max_slope is 50: steep ground is slid, not climbed (GDD "Rule table"). The
 * player is not stuck (turning moves it again), it simply cannot go straight
 * through a wall.
 *
 * Walking a straight line at a destination on this planet is the naive thing
 * that terrain like this exists to defeat, which is why C10's lap route came
 * out of a 13,920-candidate scan rather than a bearing. This test needs the
 * same treatment: a waypoint route from spawn to the camp, or a camp site
 * chosen for reachability as well as for clearance from landmarks and craters.
 *
 * It is committed red on purpose. The camp, its NPCs and their archetype
 * health are all verified real by the checks that DO run; what is unproven is
 * that a player can walk there and get shot at.
 *
 * Run: node test/t16-camp-fight.mjs   (needs `make up`)
 */
const enc=new TextEncoder(), dec=new TextDecoder(), u8=n=>new Uint8Array(n)
const frame=(t,b)=>{const o=u8(2+b.length);new DataView(o.buffer).setUint16(0,t,true);o.set(b,2);return o}
function hello(n_,tk){const n=enc.encode(n_),t=enc.encode(tk);const b=u8(2+4+n.length+4+t.length),d=new DataView(b.buffer)
 d.setUint16(0,2,true);d.setUint32(2,n.length,true);b.set(n,6);d.setUint32(6+n.length,t.length,true);b.set(t,10+n.length);return frame(1,b)}
function input(mx,my,lk,mask,seq){const b=u8(24),d=new DataView(b.buffer)
 d.setFloat32(0,mx,true);d.setFloat32(4,my,true);d.setFloat32(8,lk[0],true);d.setFloat32(12,lk[1],true);d.setFloat32(16,lk[2],true)
 d.setUint16(20,mask,true);d.setUint16(22,seq,true);return frame(3,b)}
const norm=v=>{const l=Math.hypot(...v);return v.map(x=>x/l)}
const sub=(a,b)=>[a[0]-b[0],a[1]-b[1],a[2]-b[2]]
const sleep=ms=>new Promise(r=>setTimeout(r,ms))
const ws=new WebSocket('ws://127.0.0.1:18080/ws');ws.binaryType='arraybuffer'
let myId=0;const ents=new Map(),spawns=new Map(),events=[]
ws.addEventListener('open',()=>ws.send(hello('camper','camp-'+Date.now())))
ws.addEventListener('message',ev=>{const dv=new DataView(ev.data),t=dv.getUint16(0,true),pv=new DataView(ev.data,2),p=new Uint8Array(ev.data,2)
 if(t===2)myId=pv.getUint32(8,true)
 else if(t===5)spawns.set(pv.getUint32(0,true),{type:pv.getUint16(4,true),def:dec.decode(p.subarray(10))})
 else if(t===4){const n=pv.getUint16(6,true);for(let i=0;i<n;i++){const o=8+i*54
   ents.set(pv.getUint32(o,true),{pos:[pv.getFloat32(o+4,true),pv.getFloat32(o+8,true),pv.getFloat32(o+12,true)],health:pv.getUint16(o+50,true),flags:pv.getUint8(o+52)})}}
 else if(t===7)events.push({id:pv.getUint32(0,true),ev:pv.getUint16(4,true)})})
const wait=async(f,ms=5000)=>{const t0=Date.now();while(Date.now()-t0<ms){const v=f();if(v)return v;await sleep(25)}return null}
await wait(()=>myId&&ents.size>3)
const npcs=[...spawns].filter(([,v])=>v.type===3&&v.def!=='npc.quartermaster')
const targets=[...spawns].filter(([,v])=>v.type===4)
console.log(`joined id=${myId}; camp NPCs=${npcs.length} targets=${targets.length} total spawns=${spawns.size}`)
const [gid,gdef]=npcs[0]
const gpos=ents.get(gid).pos
console.log(`nearest camp NPC ${gid} (${gdef.def}) at ${Math.hypot(...sub(gpos,ents.get(myId).pos)).toFixed(0)} m, health ${ents.get(gid).health}`)
// Unprovoked at 271 m: it must not drift.
const p0=[...ents.get(gid).pos]
await sleep(2000)
const idleDrift=Math.hypot(...sub(ents.get(gid).pos,p0))

// Sprint to the camp. Aggro radius is 22-30 m, so stop just inside it.
const me=()=>ents.get(myId).pos
let seq=1
const t0=Date.now()
while(Date.now()-t0<90000){
  const d=sub(gpos,me()); const dist=Math.hypot(...d)
  if(dist<=20) break
  const u=norm(me()); const lk=norm(sub(d,u.map(x=>x*(d[0]*u[0]+d[1]*u[1]+d[2]*u[2]))))
  ws.send(input(0,1,lk,0x0001,seq++)); await sleep(50)
}
const arrived=Math.hypot(...sub(gpos,me()))
console.log(`walked in: ${arrived.toFixed(1)} m from the NPC`)

// Stand and take it for 8 s.
const hpStart=ents.get(myId).health
const npcStart=[...npcs].map(([id])=>ents.get(id)?.pos)
events.length=0
const watchT0=Date.now()
while(Date.now()-watchT0<8000){
  const u=norm(me()); const d=sub(gpos,me())
  const lk=norm(sub(d,u.map(x=>x*(d[0]*u[0]+d[1]*u[1]+d[2]*u[2]))))
  ws.send(input(0,0,lk,0,seq++)); await sleep(50)
}
const hpEnd=ents.get(myId).health
const moved=npcs.map(([id],i)=>npcStart[i]?Math.hypot(...sub(ents.get(id).pos,npcStart[i])):0)
const anyMoved=moved.some(m=>m>1.0)
const hits=events.filter(e=>e.ev===3).length

console.log(`idle drift ${idleDrift.toFixed(3)} m | NPC movement after aggro: max ${Math.max(...moved).toFixed(1)} m`)
console.log(`player health ${hpStart} -> ${hpEnd} | hit events ${hits}`)

const checks=[
 ['camp NPCs exist', npcs.length>0],
 ['NPCs idle when unprovoked', idleDrift<0.5],
 ['player reached the camp', arrived<=20],
 ['NPCs reacted (moved or attacked)', anyMoved||hits>0||hpEnd<hpStart],
 ['player took damage', hpEnd<hpStart],
]
let bad=0
for(const [n,ok] of checks){ if(!ok){console.log(`FAIL ${n}`);bad++} }
console.log(bad?`OVERALL: FAIL (${bad}/${checks.length})`:`OVERALL: PASS (${checks.length} checks)`)
process.exit(bad?1:0)
