// The screenshot rig: the -ui* flags that stage a picture for the C60/C61
// galleries in test/out/ui/. Everything on screen is the server's real word;
// the rig only pushes the buttons a hand would, then saves the frame.
//
// A partial of Boot so it can drive the same fields the frame loop reads;
// split out so Boot.cs stays the game and this stays QA. Coroutines became
// async methods: Godot resumes an awaited continuation on the main thread,
// so a `await Wait(s)` is a `yield return new WaitForSeconds(s)`.
//
//   -uiShot <path>          save a PNG once the scene settles (after -uiShotAfter s, default 8)
//   -uiPanel <name>         open bags|sheet|map|account|journal|party|skills|debug first
//   -uiRoute <json>         walk a solved route (test/out/route-*.json) before anything else
//   -uiDemo                 stage two wounded grunts and the combat feed near spawn
//   -uiFace <kind>          aim at the nearest target|npc|hostile|player|wounded|rock|mast
//   -uiPitch <deg> [-uiYaw <deg>]  look down/up and turn, from where the rig stands
//   -uiBuy <item>           E at the faced shopkeeper, buy it, equip it (a REAL weapon)
//   -uiFireNow <secs>       re-apply -uiPitch/-uiYaw, then hold the trigger that long
//   -uiClaim                claim the priority bounty like the journal button, report the log
//   -uiDeathDemo            show the death screen (local flag only) for a shot
//   -uiGatherDemo           a local iron node + wreck ahead and the channel bar (Phase 12)
//   -uiHotbarDemo           medkit on 1 (cooling), scanner on Q, from a faked bag (Phase 13)
//   -uiShift                hold the hotbar's Shift row for the shot
//   -uiHotbarDragDemo       with -uiPanel backpack: drag the first bag cell onto slot 4, report
//   -uiSellDemo             with the shop open: sell the first -uiBuy item, report the buyback count
//   -uiBuyback              show the shop's BUYBACK tab
//   -uiRetreat <secs>       with the shop open, walk backwards that long; report whether it closed
//   -uiApproach <m>         then walk toward it until within that many metres
//   -uiReface <kind>        re-pick a target on arrival
//   -uiFire <secs>          reload and hold the trigger on it
//   -uiQuest                accept the starter mission at the board, wait for a party invite
//   -uiLamp                 swing the sun onto whatever the camera ends up looking at
//   -rigArmed               show the rig without a purchase

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public partial class Boot
    {
        private bool _rigWalk;
        private bool _rigJump;
        private bool _rigFire;
        private bool _rigLamp;
        private bool _rigAutoParty; // accept any party invite
        private bool _rigBack;      // step backwards
        private bool _rigArmed;
        private bool _rigInteract; // one frame of E
        private bool _rigDeathDemo; // -uiDeathDemo holds the dead flag against the snapshots
        private bool _rigShift;     // -uiShift holds the hotbar's shift row for a shot
        private float _detourSign = 1f;

        private async Task Wait(double seconds) =>
            await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

        /// <summary>The rig's overrides on this frame's input.</summary>
        private void RigInput(ref LocalInput li)
        {
            if (_rigWalk)
            {
                li.MoveY = 1;
                li.ActionMask |= Net.Action.Sprint;
                if (_rigJump) li.ActionMask |= Net.Action.Jump;
            }
            if (_rigFire) li.FirePressed = true;
            if (_rigInteract) { li.InteractPressed = true; _rigInteract = false; }
            if (_rigBack) li.MoveY = -1;
        }

        /// <summary>
        /// Pulls the waypoint triples out of a route JSON by hand — the
        /// shape is `{"waypoints":[[x,y,z],...]}` and this is rig-only.
        /// </summary>
        private static List<float[]> ParseWaypoints(string json)
        {
            var result = new List<float[]>();
            int at = json.IndexOf("\"waypoints\"", StringComparison.Ordinal);
            if (at < 0) return result;
            int i = json.IndexOf('[', at) + 1;
            while (i < json.Length)
            {
                int open = json.IndexOf('[', i);
                int close = open < 0 ? -1 : json.IndexOf(']', open);
                if (open < 0 || close < 0) break;
                int outerClose = json.IndexOf(']', i);
                if (outerClose < open) break;
                string[] parts = json.Substring(open + 1, close - open - 1).Split(',');
                if (parts.Length == 3)
                {
                    result.Add(new[]
                    {
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture),
                    });
                }
                i = close + 1;
            }
            return result;
        }

        private Vector3 Eye => _camera.GlobalPosition;
        private Vector3 CameraForward => -_camera.GlobalBasis.Z;

        private static bool Match(EntityView v, string arg, ushort type) => arg switch
        {
            "wounded" => v.ShowHealthBar,
            "hostile" => v.Type == EntityType.Npc && v.Label != "npc.quartermaster",
            _ => v.Type == type,
        };

        private static ushort KindOf(string arg) =>
            arg == "npc" || arg == "hostile" ? EntityType.Npc
            : arg == "player" ? EntityType.Player
            : EntityType.Target;

        /// <summary>
        /// -uiApproach: walks toward `goal` until within `closeTo` metres
        /// (the horizon is ~23 m, so anything worth photographing has to be
        /// closed to arm's reach first). Sidesteps and jumps when wedged.
        /// </summary>
        private async Task ApproachTo(Vector3 goal, float closeTo)
        {
            double deadline = Clock.Now + 150;
            _rigWalk = true;
            Vector3 lastEye = Eye;
            double lastMoveAt = Clock.Now;
            double detourUntil = 0;
            while (Clock.Now < deadline)
            {
                Vector3 eyeNow = Eye;
                Vector3 to = goal - eyeNow;
                if (to.Length() <= closeTo) break;
                // Wedged? Sidestep: aim 40 degrees off a moment,
                // alternating sides around obstacles.
                if ((eyeNow - lastEye).Length() > 0.3f) { lastEye = eyeNow; lastMoveAt = Clock.Now; }
                else if (Clock.Now - lastMoveAt > 0.8 && Clock.Now > detourUntil)
                {
                    detourUntil = Clock.Now + 3;
                    lastMoveAt = Clock.Now;
                    _detourSign = -_detourSign;
                }
                // Jump at whatever we are wedged on — a scarp
                // under max_slope yields to it.
                _rigJump = Clock.Now - lastMoveAt > 0.6 || Clock.Now < detourUntil;
                Vector3 aimPoint = goal;
                if (Clock.Now < detourUntil)
                    aimPoint = eyeNow + to.Rotated(eyeNow.Normalized(), -Mathf.DegToRad(_detourSign * 40f));
                _fps.FaceToward(eyeNow, aimPoint);
                await Wait(0.2);
            }
            _rigWalk = false;
            _rigJump = false;
            GD.Print($"ui: approached to {(goal - Eye).Length():F0} m");
        }

        /// <summary>Saves the review screenshot once the scene settles, after any staging.</summary>
        private async Task SaveUiShot(string path)
        {
            try
            {
                await StageAndShoot(path);
            }
            catch (Exception e)
            {
                GD.PushError($"uiShot failed: {e}");
                if (_quitAfter >= 0) GetTree().Quit(1);
            }
        }

        private async Task StageAndShoot(string path)
        {
            double wait = 8.0;
            string w = Arg("-uiShotAfter");
            if (w != null) wait = double.Parse(w, CultureInfo.InvariantCulture);
            await Wait(wait);
            _rigLamp = Flag("-uiLamp");

            // -uiRoute <path>: walk a solved route before facing anything —
            // straight lines on this planet wedge on scarps. Same walker as
            // -uiApproach: sprint, jump when stuck.
            string route = Arg("-uiRoute");
            if (route != null)
            {
                List<float[]> waypoints = ParseWaypoints(System.IO.File.ReadAllText(route));
                GD.Print($"ui: route has {waypoints.Count} waypoints");
                _rigWalk = true;
                foreach (float[] wp in waypoints)
                {
                    var goal = new Vector3(wp[0], wp[1], wp[2]); // sim frame is the world frame
                    double legEnd = Clock.Now + 22;
                    Vector3 lastEye = Eye;
                    double lastMove = Clock.Now;
                    while (Clock.Now < legEnd)
                    {
                        if ((goal - Eye).Length() < 6f) break;
                        if ((Eye - lastEye).Length() > 0.3f) { lastEye = Eye; lastMove = Clock.Now; }
                        _rigJump = Clock.Now - lastMove > 0.7;
                        _fps.FaceToward(Eye, goal);
                        await Wait(0.2);
                    }
                }
                _rigWalk = false;
                _rigJump = false;
                GD.Print("ui: route walked");
            }

            // -uiDemo: two local grunt bodies on the terrain ahead (same
            // Create path as a live spawn) and the same CombatFeed calls the
            // live hit event drives, so the LOOK is photographable on demand.
            if (Flag("-uiDemo"))
            {
                Vector3 eyeD = Eye;
                Vector3 upD = eyeD.Normalized();
                Vector3 fwdD = CameraForward.Slide(upD).Normalized();

                EntityView PlaceGrunt(uint id, float dist, float sideDeg, ushort health)
                {
                    // Positive sideDeg is to the RIGHT, as it read in the Unity rig.
                    Vector3 dir = fwdD.Rotated(upD, -Mathf.DegToRad(sideDeg));
                    Vector3 sd = (eyeD + dir * dist).Normalized();
                    float r = (float)_terrain.SampleRadius(Frame.ToSim(sd));
                    var v = _views.SpawnLocalDemo(id, EntityType.Npc, "npc.grunt", Frame.ToSim(sd * r), Frame.ToSim(-dir));
                    v.MaxHealth = 60;
                    v.Health = health;
                    return v;
                }

                EntityView g1 = PlaceGrunt(0x000F0001, 9f, -8f, 38);
                EntityView g2 = PlaceGrunt(0x000F0002, 13f, 14f, 12);
                await Wait(0.4);
                _fps.FaceToward(Eye, (g1.Root.GlobalPosition + g2.Root.GlobalPosition) * 0.5f + upD * 1.2f);
                await Wait(0.15);

                Vector3 up1 = g1.Root.GlobalPosition.Normalized();
                Vector3 up2 = g2.Root.GlobalPosition.Normalized();
                _combatFeed.Damage(g2.Root.GlobalPosition + up2 * 1.5f, 47, true);
                _combatFeed.Incoming(2.6);
                await Wait(0.25);
                _combatFeed.Damage(g1.Root.GlobalPosition + up1 * 1.55f, 20, false);
                _combatFeed.HitMarker(false);
                await Wait(0.12);
            }

            // -uiFace target|npc|hostile|player|wounded: aim the camera at
            // the nearest such entity, so a combat shot has something in frame.
            // -uiPitch <deg> [-uiYaw <deg>]: look down (negative) or up, and
            // turn, from wherever the rig stands -- a mouse move on demand.
            string pitchArg = Arg("-uiPitch");
            if (pitchArg != null)
            {
                float pitch = Mathf.DegToRad(float.Parse(pitchArg, CultureInfo.InvariantCulture));
                float yaw = Mathf.DegToRad(float.Parse(Arg("-uiYaw") ?? "0", CultureInfo.InvariantCulture));
                Vector3 eyeP = Eye, upP = eyeP.Normalized();
                Vector3 fwdP = CameraForward.Slide(upP).Normalized().Rotated(upP, -yaw);
                _fps.FaceToward(eyeP, eyeP + fwdP * (10f * Mathf.Cos(pitch)) + upP * (10f * Mathf.Sin(pitch)));
                await Wait(0.1);
            }

            string wantArg = Arg("-uiFace");
            if (wantArg == "mast")
            {
                // Masts are props, not entities: aim at the nearest POI mast.
                Vector3? mast = null; float mastD = float.MaxValue;
                foreach (var m in _structures.Masts)
                {
                    float d = m.pos.DistanceSquaredTo(Eye);
                    if (d < mastD) { mastD = d; mast = m.pos; }
                }
                if (mast.HasValue)
                {
                    _fps.FaceToward(Eye, mast.Value);
                    GD.Print($"ui: facing mast at {Mathf.Sqrt(mastD):F0} m");
                    string appMast = Arg("-uiApproach");
                    if (appMast != null && float.TryParse(appMast, NumberStyles.Float, CultureInfo.InvariantCulture, out float closeToMast))
                    {
                        await ApproachTo(mast.Value, closeToMast);
                        _fps.FaceToward(Eye, mast.Value);
                    }
                }
                else GD.Print("ui: no masts to face");
            }
            else if (wantArg == "rock")
            {
                // Rocks are not entities: aim at the nearest scatter placement.
                Vector3? rock = _rocks.Nearest(Eye);
                if (rock.HasValue)
                {
                    _fps.FaceToward(Eye, rock.Value);
                    GD.Print($"ui: facing rock at {Eye.DistanceTo(rock.Value):F0} m");
                    string appRock = Arg("-uiApproach");
                    if (appRock != null && float.TryParse(appRock, NumberStyles.Float, CultureInfo.InvariantCulture, out float closeToRock))
                    {
                        await ApproachTo(rock.Value, closeToRock);
                        _fps.FaceToward(Eye, rock.Value);
                    }
                }
                else GD.Print("ui: no rocks to face");
            }
            else if (wantArg != null)
            {
                ushort want = KindOf(wantArg);
                EntityView best = null;
                float bestD = float.MaxValue;
                // "wounded" can flicker out (a kill respawns the target at
                // full health), so poll for one instead of sampling once.
                for (double waited = 0; best == null && waited < 12; waited += 0.25)
                {
                    foreach (EntityView v in _views.All)
                    {
                        if (v.Root == null || v.Id == _net.EntityId) continue;
                        if (!Match(v, wantArg, want)) continue;
                        float d = (v.Root.GlobalPosition - Eye).LengthSquared();
                        if (d < bestD) { bestD = d; best = v; }
                    }
                    if (best == null) await Wait(0.25);
                }
                if (best != null)
                {
                    // A FIXED copy of the goal: a kill-and-respawn cycle can
                    // move or recycle the live Root mid-walk.
                    Vector3 goal = best.Root.GlobalPosition;
                    _fps.FaceToward(Eye, goal);
                    GD.Print($"ui: facing {wantArg} {best.Id} at {Mathf.Sqrt(bestD):F0} m");

                    // -uiApproach <m>: walk toward the faced spot until within
                    // that many metres (the horizon is ~23 m, so anything worth
                    // photographing has to be closed to arm's reach first).
                    string app = Arg("-uiApproach");
                    if (app != null && float.TryParse(app, NumberStyles.Float, CultureInfo.InvariantCulture, out float closeTo))
                    {
                        await ApproachTo(goal, closeTo);

                        // Re-pick at arrival: the thing wounded NOW may be a
                        // different entity — and -uiReface <kind> can retarget.
                        string refaceArg = Arg("-uiReface") ?? wantArg;
                        ushort refaceWant = KindOf(refaceArg);
                        EntityView again = null;
                        float againScore = float.MaxValue;
                        for (double waited = 0; again == null && waited < 12; waited += 0.25)
                        {
                            foreach (EntityView v in _views.All)
                            {
                                if (v.Root == null || v.Id == _net.EntityId) continue;
                                if (!Match(v, refaceArg, refaceWant)) continue;
                                // Hostiles rank by REMAINING HEALTH: a full
                                // grunt keeps popping for the camera.
                                float d = refaceArg == "hostile" ? -v.Health : (v.Root.GlobalPosition - Eye).LengthSquared();
                                if (d < againScore) { againScore = d; again = v; }
                            }
                            if (again == null) await Wait(0.25);
                        }
                        if (again != null && again.Root != null)
                        {
                            _fps.FaceToward(Eye, again.Root.GlobalPosition);
                            GD.Print($"ui: refaced {again.Id} health {again.Health}");

                            // -uiFire <secs>: reload, then hold the trigger,
                            // retargeting the healthiest living hostile.
                            string fire = Arg("-uiFire");
                            if (fire != null && float.TryParse(fire, NumberStyles.Float, CultureInfo.InvariantCulture, out float fireSecs))
                            {
                                _net.Send(Character.ReloadCmd(NextCmdSeq()));
                                await Wait(0.6);
                                _rigFire = true;
                                double stopAt = Clock.Now + fireSecs;
                                while (Clock.Now < stopAt)
                                {
                                    EntityView tgt = null;
                                    foreach (EntityView v in _views.All)
                                    {
                                        if (v.Root == null || v.Id == _net.EntityId) continue;
                                        if (!Match(v, refaceArg, refaceWant)) continue;
                                        if (tgt == null || v.Health > tgt.Health) tgt = v;
                                    }
                                    if (tgt != null)
                                        _fps.FaceToward(Eye, tgt.Root.GlobalPosition + Eye.Normalized() * 0.9f);
                                    await Wait(0.1);
                                }
                            }
                        }
                    }
                }
                if (!_rigFire) await Wait(1.5); // let a popup land
            }

            // -uiQuest: accept the starter mission at the board the approach
            // just reached, ask for the offer list, and auto-accept the party
            // invite a wire helper sends.
            if (Flag("-uiQuest"))
            {
                _rigAutoParty = true;

                // The approach overshoots into the NPC's face, where the
                // server's cone test rightly refuses — back off to 2.2 m.
                uint board = 0;
                for (double w2 = 0; w2 < 6; w2 += 0.15)
                {
                    board = NearestBoard();
                    if (board == 0 || !_views.TryGet(board, out var bv) || bv.Root == null) break;
                    Vector3 bp = bv.Root.GlobalPosition;
                    Vector3 bup = bp.Normalized();
                    _fps.FaceToward(Eye, bp + bup * 1.5f);
                    float d = (bp + bup * 1.7f - Eye).Length();
                    if (d >= 2.0f && d <= 2.8f) break;
                    _rigBack = d < 2.0f;
                    _rigWalk = d > 2.8f;
                    await Wait(0.15);
                    _rigBack = false;
                    _rigWalk = false;
                }
                _rigBack = false;
                _rigWalk = false;

                if (board != 0)
                {
                    for (int tries = 0; tries < 4; tries++)
                    {
                        _net.Send(Encode.Cmd(NextCmdSeq(), Op.MissionList, $"{{\"npc\":{board}}}"));
                        _net.Send(Encode.Cmd(NextCmdSeq(), Op.MissionAccept, "{\"id\":\"mission.cull\"}"));
                        await Wait(0.8);
                        if (_missionLog.State.TryGetValue("mission.cull", out var st) && st.active) break;
                    }
                }
                for (double w3 = 0; w3 < 20 && !_partyState.InParty; w3 += 0.5) await Wait(0.5);
                _journalView.Show(true);
                await Wait(0.5);
            }

            // -uiBuy <item>: E at whatever -uiFace npc lined up (the shop opens
            // off the live interaction), buy it, equip it in `primary`. The
            // real purchase, not -rigArmed's visual one, so the server will
            // resolve a fire.
            string buy = Arg("-uiBuy");
            if (buy != null)
            {
                // The interaction cone is measured against the NPC's EYE
                // (Interact.Update), not its feet, so face that.
                EntityView shop = null; float shopD = float.MaxValue;
                foreach (EntityView v in _views.All)
                {
                    if (v.Root == null || v.Type != EntityType.Npc || v.Dead) continue;
                    float d = (v.Root.GlobalPosition - Eye).LengthSquared();
                    if (d < shopD) { shopD = d; shop = v; }
                }
                if (shop != null)
                {
                    Vector3 feet = shop.Root.GlobalPosition;
                    _fps.FaceToward(Eye, feet + feet.Normalized() * 1.7f);
                    await Wait(0.2);
                }
                _rigInteract = true;
                await Wait(1.0);
                // A comma list buys and wears several: -uiBuy weapon.pulse,armor.suit.scout
                foreach (string one in buy.Split(','))
                {
                    _net.Send(_interact.BuyCmd(NextCmdSeq(), one, 0));
                    await Wait(0.5);
                    string slot = _character.Defs.SlotOf(one);
                    if (slot == "accessory") slot = string.IsNullOrEmpty(_character.Worn("accessory1")) ? "accessory1" : "accessory2";
                    if (!string.IsNullOrEmpty(slot)) _net.Send(_character.EquipCmd(NextCmdSeq(), slot, one));
                    await Wait(0.5);
                }
                GD.Print($"ui: bought {buy}: worn={string.Join(",", _character.Equipped.Values)}");
            }

            // -uiClaim: claim the priority bounty the way the journal's button
            // does, then report the log -- the accept result is what moves it.
            if (Flag("-uiClaim"))
            {
                for (double waited = 0; _missionLog.PriorityMission == null && waited < 10; waited += 0.25) await Wait(0.25);
                string pid = _missionLog.PriorityMission;
                if (pid == null) GD.Print("ui: no priority offer to claim");
                else
                {
                    _missionLog.OnAcceptSent(pid);
                    _net.Send(Encode.Cmd(NextCmdSeq(), Op.MissionAccept, $"{{\"id\":\"{pid}\"}}"));
                    await Wait(1.5);
                    bool active = _missionLog.State.TryGetValue(pid, out var st) && st.active;
                    GD.Print($"ui: claimed {pid}: active={active} priority={_missionLog.PriorityMission ?? "none"} note={_missionLog.ClaimNote ?? "none"}");
                }
            }

            // -uiFireNow <secs>: turn by -uiPitch/-uiYaw again (after any
            // facing above), then hold the trigger that long.
            string fireNow = Arg("-uiFireNow");
            if (fireNow != null && float.TryParse(fireNow, NumberStyles.Float, CultureInfo.InvariantCulture, out float holdSecs))
            {
                if (pitchArg != null)
                {
                    float pitch = Mathf.DegToRad(float.Parse(pitchArg, CultureInfo.InvariantCulture));
                    float yaw = Mathf.DegToRad(float.Parse(Arg("-uiYaw") ?? "0", CultureInfo.InvariantCulture));
                    Vector3 eyeF = Eye, upF = eyeF.Normalized();
                    Vector3 fwdF = CameraForward.Slide(upF).Normalized().Rotated(upF, -yaw);
                    _fps.FaceToward(eyeF, eyeF + fwdF * (10f * Mathf.Cos(pitch)) + upF * (10f * Mathf.Sin(pitch)));
                    await Wait(0.2);
                }
                _rigFire = true;
                await Wait(holdSecs);
                _rigFire = false;
            }

            // -uiKitDemo: two kit pieces 7 m ahead through the props path
            // (AssetRegistry.Attach), so the wall render can be photographed
            // at spawn instead of after a 250 m walk.
            if (Flag("-uiKitDemo"))
            {
                Vector3 eyeK = Eye, upK = eyeK.Normalized();
                Vector3 fwdK = CameraForward.Slide(upK).Normalized();
                Vector3 rightK = fwdK.Cross(upK).Normalized();
                foreach (var (asset, off) in new[] { ("struct.wall4.scrap", -3f), ("struct.tower.colony", 4f) })
                {
                    Vector3 ground = (eyeK + fwdK * 7f + rightK * off).Normalized();
                    ground *= (float)_terrain.SampleRadius(Frame.ToSim(ground));
                    var holder = new Node3D { Name = "kitdemo", Transform = new Transform3D(Frame.OrientationBasis(Frame.ToSim(ground), Frame.ToSim(-fwdK)), ground) };
                    AddChild(holder);
                    _assets.Attach(asset, holder, null);
                }
                await Wait(0.3);
            }

            // -uiGatherDemo: a local iron node 3 m ahead (never in a snapshot)
            // and the channel bar half full, so C127's node model, prompt
            // and bar can be photographed at spawn without a drill.
            if (Flag("-uiGatherDemo"))
            {
                Vector3 eyeG = Eye, upG = eyeG.Normalized();
                Vector3 fwdG = CameraForward.Slide(upG).Normalized();
                Vector3 groundG = (eyeG + fwdG * 3f).Normalized();
                groundG *= (float)_terrain.SampleRadius(Frame.ToSim(groundG));
                var full = _views.SpawnLocalDemo(910001, EntityType.Node, "node.ore.iron", Frame.ToSim(groundG), Frame.ToSim(-fwdG));
                full.Health = full.MaxHealth = 5; // a local view starts at 0 = depleted
                Vector3 depletedG = (eyeG + fwdG * 4f + fwdG.Cross(upG).Normalized() * 2.2f).Normalized();
                depletedG *= (float)_terrain.SampleRadius(Frame.ToSim(depletedG));
                var dep = _views.SpawnLocalDemo(910002, EntityType.Node, "node.wreck", Frame.ToSim(depletedG), Frame.ToSim(-fwdG));
                dep.Root.Scale = Vector3.One * 0.6f;
                _channelStart = Clock.Now - 1.6;
                _channelEnd = Clock.Now + 1.4;
                _channelLabel = "drilling";
                await Wait(0.3);
            }


            // -uiSellDemo: with the shop open (after -uiBuy a,b — the first
            // is unworn once the second is), sell the first the way a bag
            // right-click does, wait for the reply; -uiBuyback then shows the
            // shop's BUYBACK tab for the shot.
            if (Flag("-uiSellDemo") && _shopView.Open)
            {
                string first = (Arg("-uiBuy") ?? "").Split(',')[0];
                _net.Send(_interact.SellCmd(NextCmdSeq(), first, 1));
                await Wait(1.0);
                GD.Print($"ui: sold {first}: buyback={_interact.Buyback?.Length ?? 0} entries");
            }
            if (Flag("-uiBuyback") && _shopView.Open) { _shopView.ShowBuyback(true); await Wait(0.3); }

            // -uiRetreat <secs>: with the shop open, walk backwards that long
            // and report whether the counter closed behind you (ui_close_dist).
            if (Arg("-uiRetreat") is string retreat)
            {
                double secs = double.Parse(retreat, CultureInfo.InvariantCulture);
                bool before = _interact.ShopOpen;
                uint counter = _interact.ShopNpc; // cleared by the close; keep it for the distance
                float DistTo() => _views.TryGet(counter, out var qv) && qv.Root != null ? Frame.ToGodot(_predictor.State.Pos).DistanceTo(qv.Root.GlobalPosition) : -1f;
                float d0 = DistTo();
                _rigBack = true;
                await Wait(secs);
                _rigBack = false;
                await Wait(0.3);
                GD.Print($"ui: retreat {secs:0.#} s: shop open {before} -> {_interact.ShopOpen}, {d0:0.0} -> {DistTo():0.0} m from the counter");
            }

            // -uiShopScrollDemo: with the shop open (after -uiBuy), scroll the
            // stock to the bottom, buy a cell the way the button does, wait for
            // the reply's rebuild, and report where the list is.
            if (Flag("-uiShopScrollDemo") && _shopView.Open)
            {
                _shopView.ScrollTo(9999);
                await Wait(0.3);
                int before = _shopView.ScrollOffset;
                _net.Send(_interact.BuyCmd(NextCmdSeq(), "ammo.cell", 0));
                _shopView.Rebuild();
                await Wait(1.0);
                GD.Print($"ui: shop scroll {before} -> {_shopView.ScrollOffset}");
            }

            // -uiDeathDemo: the death screen without dying -- local flag only,
            // the way -uiDemo stages grunts. Nothing is sent.
            if (Flag("-uiDeathDemo"))
            {
                _rigDeathDemo = true;
                _hud.Dead = true;
                _hud.DeadSince = Clock.Now - 1.6;
                await Wait(0.2);
            }

            // -uiPanel: open that panel LAST, so the gallery can capture the
            // modals without simulated key presses -- and after any -uiBuy,
            // since a modal blocks the E that opens the shop. Shop is
            // excluded: it only opens off a live NPC interaction.
            string panel = Arg("-uiPanel");
            if (panel != null)
            {
                foreach (string one in panel.Split(',')) OpenUiPanel(one); // a comma list opens several
                await Wait(1.0); // refresh round trip

                // -uiDragDemo: grab the open panel by its header and drag it
                // 300 px right, 120 px down through Godot's own input path,
                // then report where it landed and what was saved.
                if (Flag("-uiDragDemo"))
                {
                    UI.ModalView target = panel.Split(',')[0] switch { "backpack" or "bags" => _bagsView, "journal" => _journalView, "skills" => _skillsView, "party" => _partyView, _ => _sheetView };
                    Vector2 from = target.HeaderCentre, to = from + new Vector2(300, 120);
                    Vector2 before = target.Position;
                    Godot.Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from });
                    await Wait(0.1);
                    for (int i = 1; i <= 6; i++)
                    {
                        Vector2 at = from.Lerp(to, i / 6f);
                        Godot.Input.WarpMouse(at);
                        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                        await Wait(0.05);
                    }
                    Godot.Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to });
                    await Wait(0.2);
                    var cf = new ConfigFile(); cf.Load("user://sa.cfg");
                    GD.Print($"ui: dragged {panel} {before} -> {target.Position}, saved={cf.HasSection("panels")}");
                }
            }

            // -uiHotbarDemo: a medkit on 1 (two in a faked bag), the scanner on
            // Q worn in GADGET, a cooldown sweep running on 1, nothing sent.
            if (Flag("-uiHotbarDemo"))
            {
                await Wait(1.0); // after the join's and the panel's inventory replies, which would overwrite the faked bag
                _character.OnWallet("{\"credits\":448,\"inventory\":[{\"item\":\"consumable.medkit\",\"qty\":2},{\"item\":\"gadget.scanner\",\"qty\":1},{\"item\":\"weapon.pulse\",\"qty\":1},{\"item\":\"mod.mag\",\"qty\":1},{\"item\":\"ammo.cell\",\"qty\":90}],\"equipped\":{\"gadget\":\"gadget.scanner\",\"primary\":\"weapon.pulse\",\"mod\":\"mod.mag\"}}");
                // The scan's compass half, locally: every real node in view pinged for 20 s.
                foreach (EntityView v in _views.All)
                    if (v.Type == EntityType.Node && v.Root != null)
                        _scanPings.Add((v.Label.Contains("copper") ? "COPPER" : v.Label.Contains("wreck") ? "WRECK" : "IRON", v.Root.GlobalPosition, Clock.Now + 20));
                _hotbar.Refs[0] = new UI.HotbarRef { Kind = "item", Id = "consumable.medkit" };
                _hotbar.Refs[5] = new UI.HotbarRef { Kind = "ability", Id = "gadget.scanner" };
                _hotbar.Refs[12] = new UI.HotbarRef { Kind = "item", Id = "consumable.medkit" };
                _hotbarView.SetCooldown("consumable.medkit", 8, Clock.Now - 3);
                if (_sheetView.Open) _sheetView.Rebuild();
                if (_bagsView.Open) _bagsView.Rebuild();
                await Wait(0.2);

                // -uiHotbarDragDemo (with -uiPanel backpack): drag the first bag
                // cell (the medkit) onto the 4 slot through Godot's own drag and
                // drop, then report the slot and what was saved.
                if (Flag("-uiHotbarDragDemo") && _bagsView.Open)
                {
                    Vector2 from = _bagsView.FirstCellCentre, to = _hotbarView.CellCentre(3);
                    Godot.Input.WarpMouse(from);
                    // Godot's drag-and-drop starts on the viewport's own drag
                    // threshold, which parsed events never trip; ForceDrag is
                    // the engine's programmatic start, with the cell's own payload.
                    Control cell = _bagsView.FirstCell;
                    cell.ForceDrag(cell._GetDragData(Vector2.Zero), new Label { Text = "medkit" });
                    await Wait(0.1);
                    for (int i = 1; i <= 8; i++)
                    {
                        Vector2 at = from.Lerp(to, i / 8f);
                        Godot.Input.WarpMouse(at);
                        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
                        await Wait(0.05);
                    }
                    Godot.Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to });
                    await Wait(0.3);
                    var cf = new ConfigFile(); cf.Load("user://sa.cfg");
                    GD.Print($"ui: hotbar drag {from} -> {to}: slot4={_hotbar.Refs[3]} saved={cf.GetValue("hotbar", "slot3", "").AsString()}");
                }
            }
            _rigShift = Flag("-uiShift");

            if (_rigLamp && _sun != null)
            {
                _sun.LookAtFromPosition(Vector3.Zero, CameraForward, Eye.Normalized());
            }
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            Image img = GetViewport().GetTexture().GetImage();
            Error err = img.SavePng(path);
            GD.Print($"uiShot: {path} {img.GetWidth()}x{img.GetHeight()} {err}");
        }
    }
}
