package terrain

import (
	"math"
	"math/rand/v2"
)

// GDD "M1 terrain generation" parameters. The algorithm is advisory; the
// binding contract is the shape character in "Must hold", verified by
// Report() against the generated field.
const (
	baseFreq          = 1.5
	baseOctaves       = 4
	baseAmp           = 14.0
	highlandThreshold = 0.15
	highlandWindow    = 0.05 // smoothstep half-width around the threshold
	plainDamp         = 0.35
	ridgeFreq         = 3.0
	ridgeOctaves      = 5
	ridgeAmp          = 20.0
	detailFreq        = 12.0
	detailOctaves     = 2
	detailAmp         = 1.2

	craterCountMin   = 6
	craterCountMax   = 12 // inclusive
	craterDiamMin    = 20.0
	craterDiamMax    = 60.0
	craterDepthRatio = 0.15
	craterRimRatio   = 0.05
	craterMinSep     = 1.2

	spawnFlatRadius = 25.0
	spawnFlatBlend  = 15.0
	// spawnBandEnd is the outer edge of the spawn flat disc + blend band,
	// in angular distance from the spawn direction.
	spawnBandEnd = (spawnFlatRadius + spawnFlatBlend) / PlanetRadius
	// normalEps is the slope-measurement neighbourhood (2°, ≈5.2 m at the
	// surface) used by the spawn-band slope check.
	normalEps = 2.0 * math.Pi / 180
	// The relief suppression ramps 0→1 over [spawnSupIn, spawnSupOut]: it
	// starts one neighbourhood past the blend band so a slope measured at
	// the band edge (±normalEps neighbourhood) still sees flat ground,
	// and it is one blend width wide.
	spawnSupIn  = (spawnFlatRadius + spawnFlatBlend + normalEps*PlanetRadius) / PlanetRadius
	spawnSupOut = spawnSupIn + spawnFlatBlend/PlanetRadius

	landmarkJitterDeg = 15.0
	// LandmarkMinSep is the GDD landmark_min_sep: jitter may not bunch two
	// landmarks closer than this. Base face directions are 90° apart and
	// jitter is ≤15°, so this holds by construction; Report measures it.
	LandmarkMinSep     = 60.0 * math.Pi / 180
	beaconOffsetMeters = 54.0
)

// SpawnDir is the GDD spawn_dir (0,1,0).
var SpawnDir = Vec{0, 1, 0}

// LandmarkPos records where a landmark was placed (for the "distinct and
// separated" constraint check and debugging).
type LandmarkPos struct {
	Name string
	Dir  Vec
}

// ---- noise ----

func mix64(z uint64) uint64 {
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9
	z = (z ^ (z >> 27)) * 0x94d049bb133111eb
	return z ^ (z >> 31)
}

// hash3 maps a lattice point to a deterministic value in [0, 1).
func hash3(x, y, z int32, seed uint64) float64 {
	h := mix64(uint64(uint32(x))) ^
		mix64(uint64(uint32(y))+0x9e3779b97f4a7c15) ^
		mix64(uint64(uint32(z))+0x517cc1b727220a95) ^
		seed
	return float64(mix64(h)>>11) * (1.0 / (1 << 53))
}

func smoothstep(lo, hi, x float64) float64 {
	if x <= lo {
		return 0
	}
	if x >= hi {
		return 1
	}
	t := (x - lo) / (hi - lo)
	return t * t * (3 - 2*t)
}

// valueNoise3 is trilinear value noise with smoothstep interpolation,
// evaluated at d·freq (d a unit direction vector). Result in [0, 1].
func valueNoise3(d Vec, freq float64, seed uint64) float64 {
	x, y, z := d[0]*freq, d[1]*freq, d[2]*freq
	xi, yi, zi := int32(math.Floor(x)), int32(math.Floor(y)), int32(math.Floor(z))
	fx, fy, fz := x-float64(xi), y-float64(yi), z-float64(zi)
	sx := fx * fx * (3 - 2*fx)
	sy := fy * fy * (3 - 2*fy)
	sz := fz * fz * (3 - 2*fz)
	c000 := hash3(xi, yi, zi, seed)
	c100 := hash3(xi+1, yi, zi, seed)
	c010 := hash3(xi, yi+1, zi, seed)
	c110 := hash3(xi+1, yi+1, zi, seed)
	c001 := hash3(xi, yi, zi+1, seed)
	c101 := hash3(xi+1, yi, zi+1, seed)
	c011 := hash3(xi, yi+1, zi+1, seed)
	c111 := hash3(xi+1, yi+1, zi+1, seed)
	x00 := c000 + (c100-c000)*sx
	x10 := c010 + (c110-c010)*sx
	x01 := c001 + (c101-c001)*sx
	x11 := c011 + (c111-c011)*sx
	y0 := x00 + (x10-x00)*sy
	y1 := x01 + (x11-x01)*sy
	return y0 + (y1-y0)*sz
}

// fbm is fractal value noise (0.5 persistence, lacunarity 2), normalised to
// [0, 1].
func fbm(d Vec, freq float64, octaves int, seed uint64) float64 {
	total, amp, norm := 0.0, 1.0, 0.0
	for i := range octaves {
		total += amp * valueNoise3(d, freq, seed+uint64(i)*0x9e3779b97f4a7c15)
		norm += amp
		amp *= 0.5
		freq *= 2.0
	}
	return total / norm
}

// ridged is ridged fractal noise (1 − |2n − 1| per octave), normalised to
// [0, 1]. Peaks where the noise crosses zero, so ranges come out as
// connected ridgelines with valleys between.
func ridged(d Vec, freq float64, octaves int, seed uint64) float64 {
	total, amp, norm := 0.0, 1.0, 0.0
	for i := range octaves {
		n := 2*valueNoise3(d, freq, seed+uint64(i)*0x9e3779b97f4a7c15) - 1
		total += amp * (1 - math.Abs(n))
		norm += amp
		amp *= 0.5
		freq *= 2.0
	}
	return total / norm
}

// ---- craters ----

type crater struct {
	dir    Vec     // unit centre
	rimRad float64 // angular radius (rim at t = 1)
	depth  float64 // m
	rimH   float64 // m
}

// craterProfile is the bowl-and-rim displacement (metres) at normalised
// angular distance t (t = 1 at the rim). Bowl: deepest at centre, easing to
// zero at the rim; raised rim centred at t = 1; ejecta easing to zero by
// t = 1.4. Continuous at t = 1 (both sides meet at rimH).
func craterProfile(t, depth, rimH float64) float64 {
	switch {
	case t >= 1.4:
		return 0
	case t < 1:
		b := (1 - t*t)
		b *= b
		return -depth*b + rimH*smoothstep(0.7, 1, t)
	default:
		x := (t - 1) / 0.12
		return rimH * math.Exp(-x*x)
	}
}

// randomDir draws a uniform unit vector.
func randomDir(rng *rand.Rand) Vec {
	z := rng.Float64()*2 - 1
	phi := rng.Float64() * 2 * math.Pi
	r := math.Sqrt(1 - z*z)
	return Vec{r * math.Cos(phi), r * math.Sin(phi), z}
}

func angularDist(a, b Vec) float64 {
	c := a.Dot(b)
	if c > 1 {
		c = 1
	}
	if c < -1 {
		c = -1
	}
	return math.Acos(c)
}

// tangentOf returns a seeded-random unit tangent of d.
func tangentOf(d Vec, rng *rand.Rand) Vec {
	r := randomDir(rng)
	return Normalize(r.Sub(d.Scale(r.Dot(d))))
}

// placeCraters scatters craters by rejection sampling with the
// crater_min_sep spacing rule, landmark-footprint rejection, and a
// relief-readability rule: a crater cut into a slope reads as a scar, so
// candidates whose underlying (pre-crater) terrain at the centre sits
// more than the bowl can absorb above the rim mean are rejected.
func placeCraters(rng *rand.Rand, lm []landmark, seed uint64) []crater {
	count := craterCountMin + rng.IntN(craterCountMax-craterCountMin+1)
	var out []crater
	for range count {
		diam := craterDiamMin + rng.Float64()*(craterDiamMax-craterDiamMin)
		rimRad := diam / 2 / PlanetRadius // angular radius
		depth := craterDepthRatio * diam
		rimH := craterRimRatio * diam
		placed := false
		for attempt := 0; attempt < 200 && !placed; attempt++ {
			d := randomDir(rng)
			ok := true
			// The spawn plain (flat disc + blend band) is applied last and
			// flattens everything it reaches; a crater whose full extent
			// (1.4 rim radii) reaches the flat zone would be flattened and
			// could never be field-confirmed, so reject it.
			if angularDist(d, SpawnDir) < spawnSupIn+1.4*rimRad {
				continue
			}
			for _, o := range out {
				if angularDist(d, o.dir) < craterMinSep*(rimRad+o.rimRad) {
					ok = false
					break
				}
			}
			if ok {
				for _, l := range lm {
					if angularDist(d, l.dir) < rimRad+l.footprint {
						ok = false
						break
					}
				}
			}
			if !ok {
				continue
			}
			// Readability: the bowl must survive the underlying slope.
			if underTilt(d, rimRad, seed, lm) > 0.15*depth+rimH {
				continue
			}
			out = append(out, crater{
				dir:    d,
				rimRad: rimRad,
				depth:  depth,
				rimH:   rimH,
			})
			placed = true
		}
	}
	return out
}

// underTilt returns how much higher the pre-crater underlying terrain is
// at the crater centre than the mean over the rim circle — the part of a
// crater's profile depth a slope eats.
func underTilt(d Vec, rimRad float64, seed uint64, lm []landmark) float64 {
	under := func(q Vec) float64 { return radiusAt(q, seed, nil, lm) }
	uc := under(d)
	var k Vec
	if math.Abs(d[0]) <= 0.9 {
		k = Vec{1, 0, 0}
	} else {
		k = Vec{0, 1, 0}
	}
	du := k.Dot(d)
	e1 := Normalize(k.Sub(d.Scale(du)))
	e2 := Cross(d, e1)
	const n = 16
	sum := 0.0
	for j := range n {
		a := 2 * math.Pi * float64(j) / n
		q := d.Scale(math.Cos(rimRad)).Add(e1.Scale(math.Sin(rimRad) * math.Cos(a))).Add(e2.Scale(math.Sin(rimRad) * math.Sin(a)))
		sum += under(Normalize(q))
	}
	return uc - sum/float64(n)
}

// ---- landmarks ----

type landmark struct {
	name      string
	dir       Vec     // unit centre
	footprint float64 // angular radius, for crater rejection
	apply     func(d Vec, cur float64) float64
}

// rotateAround rotates unit vector d by angle a (radians) around unit axis k
// (Rodrigues' formula).
func rotateAround(d, k Vec, a float64) Vec {
	c, s := math.Cos(a), math.Sin(a)
	return d.Scale(c).Add(Cross(k, d).Scale(s)).Add(k.Scale(d.Dot(k) * (1 - c)))
}

// EyeHeightMeters is the GDD eye_height (1.7 m): where a standing
// character's eye sits above its feet. Defined once, here, because terrain
// is the package every consumer already imports. Server-authoritative on
// purpose — hitscan origins and line-of-sight rays (sim.ResolveShot, ai's
// gunners, server's interact cone) must never come from a client-supplied
// eye position. Used locally by the landmark visibility cap.
const EyeHeightMeters = 1.7

// visibleCap returns the GDD angular visibility cap (half-angle, radians)
// for an object at absolute radius H: an object rising H above the
// nominal radius is visible from an angular distance of
// acos(R/(R+H)) + acos(R/(R+eye)) (GDD "Landmarks").
func visibleCap(H float64) float64 {
	return math.Acos(PlanetRadius/H) + math.Acos(PlanetRadius/(PlanetRadius+EyeHeightMeters))
}

// unitLattice returns a dense set of unit directions (every cube-face
// grid point, 6 × 65 × 65) used to score landmark visibility coverage at
// placement time.
func unitLattice() []Vec {
	lat := make([]Vec, 0, NumFaces*FaceGrid*FaceGrid)
	for face := range NumFaces {
		for row := range FaceGrid {
			v := 2*float64(row)/float64(FaceGrid-1) - 1
			for col := range FaceGrid {
				u := 2*float64(col)/float64(FaceGrid-1) - 1
				lat = append(lat, DirOf(face, u, v))
			}
		}
	}
	return lat
}

// pickDir chooses where to place the next landmark on its cube face. The
// objective is the GDD coverage model: maximise the union of the six
// visibility caps over the surface ("the octahedral spread wastes almost
// nothing"), not plain separation — the caps sum to 66.5% of the sphere,
// so it is overlap that costs coverage. Candidates jitter 0–15° from the
// face centre (GDD landmark_jitter, seeded); the hard constraint is
// LandmarkMinSep (60°) between landmark centres. Among candidates within
// 0.1% of the surface of the best it draws randomly, so worlds still
// differ per seed. The face centre itself is always eligible, so a
// placement always exists.
func pickDir(rng *rand.Rand, base Vec, placed []Vec, H float64, lat []Vec, covered []bool, coveredCount int) (Vec, int) {
	cosCap := math.Cos(visibleCap(H))
	k1 := tangentOf(base, rng)
	k2 := Normalize(Cross(base, k1))
	cands := []Vec{base}
	for _, jitDeg := range []float64{7.5, 15.0} {
		jit := jitDeg * math.Pi / 180
		const n = 24
		for j := range n {
			a := 2 * math.Pi * float64(j) / n
			axis := Normalize(k1.Scale(math.Cos(a)).Add(k2.Scale(math.Sin(a))))
			cands = append(cands,
				Normalize(rotateAround(base, axis, jit)),
				Normalize(rotateAround(base, axis, -jit)))
		}
	}
	tol := 0.001 * float64(len(lat))
	best := -1
	gains := make([]int, len(cands))
	var bestIdx []int
	for i, d := range cands {
		ok := true
		for _, p := range placed {
			if angularDist(d, p) < LandmarkMinSep {
				ok = false
				break
			}
		}
		if !ok {
			continue
		}
		g := 0
		for j, q := range lat {
			if !covered[j] && q.Dot(d) >= cosCap {
				g++
			}
		}
		gains[i] = g
		if g > best {
			best = g
			bestIdx = bestIdx[:0]
			bestIdx = append(bestIdx, i)
		} else if float64(g) >= float64(best)-tol {
			bestIdx = append(bestIdx, i)
		}
	}
	idx := bestIdx[rng.IntN(len(bestIdx))]
	d := cands[idx]
	// Commit the winner's cap to the coverage mask.
	for j, q := range lat {
		if !covered[j] && q.Dot(d) >= cosCap {
			covered[j] = true
		}
	}
	return d, coveredCount + gains[idx]
}

// spireProfile returns the spire surface radius at angular distance theta
// from the centre, given the local terrain radius cur. The peak is an
// absolute radius (GDD landmark table); the sides ease from the peak down
// to the terrain itself over the base, so the base edge carries no step
// (the smoothstep derivative is zero at the base, C1-continuous). Returns
// cur unchanged outside the base.
func spireProfile(cur, peak, baseAng, theta float64) float64 {
	if theta >= baseAng {
		return cur
	}
	s := theta / baseAng
	return peak + (cur-peak)*smoothstep(0, 1, s*s)
}

// makeLandmarks places the six unique landmarks (GDD "Landmarks"): the five
// shape landmarks on the non-spawn cube-face directions with seeded jitter,
// and the Home Beacon on the spawn face at beaconOffsetMeters from the spawn
// centre.
func makeLandmarks(rng *rand.Rand) []landmark {
	faces := []Vec{
		{1, 0, 0}, {-1, 0, 0}, {0, -1, 0}, {0, 0, 1}, {0, 0, -1},
	}
	rng.Shuffle(len(faces), func(i, j int) { faces[i], faces[j] = faces[j], faces[i] })

	// The Home Beacon is fixed (GDD: dir fixed, no jitter), so its
	// position is known before any jittered landmark is placed; it seeds
	// both the 60° separation constraint and the coverage mask.
	off := beaconOffsetMeters / PlanetRadius
	beaconDir := Normalize(SpawnDir.Add(Vec{1, 0, 0}.Scale(off)))
	lat := unitLattice()
	covered := make([]bool, len(lat))
	coveredCount := 0
	{
		cosCap := math.Cos(visibleCap(165.0))
		for i, q := range lat {
			if q.Dot(beaconDir) >= cosCap {
				covered[i] = true
				coveredCount++
			}
		}
	}
	placed := []Vec{beaconDir}

	var lm []landmark

	// The Spire: peak 188 m, base ⌀ 30 m, deliberately unclimbable.
	spireDir, coveredCount := pickDir(rng, faces[0], placed, 188.0, lat, covered, coveredCount)
	placed = append(placed, spireDir)
	lm = append(lm, landmark{
		name:      "spire",
		dir:       spireDir,
		footprint: 15.0/PlanetRadius + 0.05,
		apply: func(p Vec, cur float64) float64 {
			return math.Max(cur, spireProfile(cur, 188.0, 15.0/PlanetRadius, angularDist(p, spireDir)))
		},
	})

	// Twin Peaks: 180/176 m, 45 m apart, reads as a pair from every angle.
	// Placement is scored as the pair's single visibility cap (GDD: one
	// landmark, one cap) at the 180 m peak.
	twinDir, coveredCount := pickDir(rng, faces[1], placed, 180.0, lat, covered, coveredCount)
	axis := tangentOf(twinDir, rng)
	p1 := Normalize(twinDir.Add(axis.Scale(22.5 / PlanetRadius)))
	p2 := Normalize(twinDir.Sub(axis.Scale(22.5 / PlanetRadius)))
	placed = append(placed, twinDir)
	lm = append(lm, landmark{
		name:      "twinpeaks",
		dir:       twinDir,
		footprint: (22.5+10.0)/PlanetRadius + 0.05,
		apply: func(p Vec, cur float64) float64 {
			r := math.Max(cur, spireProfile(cur, 180.0, 10.0/PlanetRadius, angularDist(p, p1)))
			return math.Max(r, spireProfile(cur, 176.0, 10.0/PlanetRadius, angularDist(p, p2)))
		},
	})

	// The Mesa: flat top ⌀ 40 m at 172 m, steep sides easing into the
	// terrain (no step at the base edge), one walkable ramp (the side
	// spreads over 4× its width inside a corridor along the ramp axis →
	// ~29° slope there, ~66° on the cliffs).
	mesaDir, coveredCount := pickDir(rng, faces[2], placed, 172.0, lat, covered, coveredCount)
	placed = append(placed, mesaDir)
	ramp := tangentOf(mesaDir, rng)
	pole := Cross(mesaDir, ramp) // pole of the great circle through d along ramp
	topAng := 20.0 / PlanetRadius
	sideAng := 10.0 / PlanetRadius
	lm = append(lm, landmark{
		name:      "mesa",
		dir:       mesaDir,
		footprint: topAng + sideAng*4 + 0.05,
		apply: func(p Vec, cur float64) float64 {
			theta := angularDist(p, mesaDir)
			if theta >= topAng+sideAng*4 {
				return cur
			}
			if theta < topAng {
				return math.Max(cur, 172.0)
			}
			width := sideAng
			if math.Abs(p.Dot(pole)) < math.Sin(0.25) { // inside the ramp corridor
				width = sideAng * 4
			}
			s := smoothstep(0, 1, (theta-topAng)/width)
			return math.Max(cur, 172.0-(172.0-cur)*s)
		},
	})

	// The Great Crater: ⌀ 120 m, rim 170 m, floor 132 m. The interior is a
	// profile in absolute radii (132 m at the centre easing to the 170 m
	// rim), so the floor holds even where the raw terrain is higher; the
	// outer wall eases from the rim back down to the terrain, so the rim
	// joins whatever is underneath with no step.
	craterDir, coveredCount := pickDir(rng, faces[3], placed, 170.0, lat, covered, coveredCount)
	placed = append(placed, craterDir)
	rimAng := 60.0 / PlanetRadius
	outerW := 25.0 / PlanetRadius
	lm = append(lm, landmark{
		name:      "greatcrater",
		dir:       craterDir,
		footprint: rimAng + outerW + 0.05,
		apply: func(p Vec, cur float64) float64 {
			theta := angularDist(p, craterDir)
			if theta >= rimAng+outerW {
				return cur
			}
			if theta < rimAng {
				s := smoothstep(0, 1, theta/rimAng)
				return 132.0 + 38.0*s
			}
			s := smoothstep(0, 1, (theta-rimAng)/outerW)
			return math.Max(cur, 170.0-(170.0-math.Min(cur, 170.0))*s)
		},
	})

	// The Notch: a 60 m walkable canyon cut clean through a ridge: floor
	// 138 m, walls rising to 170 m, then easing back into the terrain so
	// the feature has no step edge (a far-end fade over the last 15 m of
	// the 1 rad cap). The profile states absolute radii (GDD landmark
	// table): on low ground the floor is raised to 138 m and the walls
	// rise to 170 m; on a ridge the channel is cut down to them. The
	// channel runs along chAxis; width is the angular distance from the
	// channel centreline (the great circle through d along chAxis).
	notchDir, coveredCount := pickDir(rng, faces[4], placed, 170.0, lat, covered, coveredCount)
	placed = append(placed, notchDir)
	chAxis := tangentOf(notchDir, rng)
	chPole := Cross(notchDir, chAxis)
	halfW := 30.0 / PlanetRadius
	lm = append(lm, landmark{
		name:      "notch",
		dir:       notchDir,
		footprint: 1.0,
		apply: func(p Vec, cur float64) float64 {
			ang := angularDist(p, notchDir)
			if ang >= 1.0 {
				return cur
			}
			fade := 1.0
			if ang > 0.9 {
				fade = 1.0 - smoothstep(0, 1, (ang-0.9)/0.1)
			}
			sraw := math.Abs(p.Dot(chPole)) / halfW
			var r float64
			switch {
			case sraw < 0.8:
				r = 138.0
			case sraw <= 1.6:
				r = 138.0 + 32.0*smoothstep(0.8, 1.6, sraw)
			case sraw <= 2.4:
				s := smoothstep(0, 1, (sraw-1.6)/0.8)
				r = 170.0 + (cur-170.0)*s
			default:
				r = cur
			}
			return cur + (r-cur)*fade
		},
	})

	// Home Beacon: slender pillar, base ⌀ 12 m, peak 165 m, 54 m from the
	// spawn centre (clear of the 25 m flat disc and its 15 m blend band;
	// the GDD recipe says 50 m, but the 50 m pillar's ~68° base flank at
	// 44-56 m from spawn enters the pinned 2° slope neighbourhood of the
	// 39 m band edge and fails the binding walkable-band constraint — see
	// QA report). The base eases into the terrain, so the base edge
	// carries no step.
	lm = append(lm, landmark{
		name:      "beacon",
		dir:       beaconDir,
		footprint: 6.0/PlanetRadius + 0.05,
		apply: func(p Vec, cur float64) float64 {
			theta := angularDist(p, beaconDir)
			baseAng := 6.0 / PlanetRadius
			if theta >= baseAng {
				return cur
			}
			s := theta / baseAng
			s = s * s * s // flat narrow top, steep narrow base
			return math.Max(cur, 165.0-(165.0-cur)*smoothstep(0, 1, s))
		},
	})

	return lm
}

// ---- generation ----

// radiusAt evaluates the layered radius field (steps 1–6 of GDD "M1 terrain
// generation") at a unit direction.
func radiusAt(d Vec, seed uint64, craters []crater, lm []landmark) float64 {
	// 1. Base relief: continent-scale lowlands vs highlands, ±baseAmp.
	baseN := fbm(d, baseFreq, baseOctaves, seed)

	// 2. Highland mask: mountains grow above the threshold; plains below
	// it have their relief damped by plainDamp.
	mask := smoothstep(highlandThreshold-highlandWindow, highlandThreshold+highlandWindow, baseN)

	// The spawn zone suppresses ALL relief (base, mountains, detail) across
	// the flat disc and its blend band, so the spawn plain meets genuinely
	// flat ground, not a mountainside. Suppression is mandatory, not just
	// for ridges: on the small circles around spawn the azimuthal
	// derivative of the continents is amplified by
	// planet_radius/arc_radius (~4× at the band edge), so relief that is
	// walkable in the open would slope past max_slope through the band.
	// Relief returns over one blend width beyond the band; that ramp is the
	// world's first mountainside and is deliberately unwalkable in places.
	angSpawn := angularDist(d, SpawnDir)
	spawnMask := smoothstep(spawnSupIn, spawnSupOut, angSpawn)

	base := (baseN - 0.5) * 2 * baseAmp * spawnMask

	// 3. Mountains: ridged fractal × highland mask × spawn mask.
	ridge := (2*ridged(d, ridgeFreq, ridgeOctaves, seed) - 1) * ridgeAmp * mask * spawnMask

	// 4. Detail: small-scale roughness, damped on plains.
	detailScale := plainDamp + (1-plainDamp)*mask
	detail := (fbm(d, detailFreq, detailOctaves, seed+0xABCD)*2 - 1) * detailAmp * detailScale * spawnMask

	r := PlanetRadius + base + ridge + detail

	// 5. Craters: subtractive bowls + raised rims, after mountains.
	for i := range craters {
		c := &craters[i]
		t := angularDist(d, c.dir) / c.rimRad
		if t < 1.4 {
			r += craterProfile(t, c.depth, c.rimH)
		}
	}

	// 6. Landmarks: max() for rising shapes, min() for excavated ones —
	// each states its own absolute radius, so they never stack with the
	// noise amplitude.
	for i := range lm {
		r = lm[i].apply(d, r)
	}
	return r
}

// quantise clamps to [RadiusMin, RadiusMax] and maps to the u16 wire code's
// exact decoded value (what the client computes from the same code).
func quantise(r float64, clamped *int) float64 {
	if r < RadiusMin || r > RadiusMax {
		*clamped++
		r = clampRadius(r)
	}
	code := uint16(math.Round((r - RadiusMin) / (RadiusMax - RadiusMin) * 65535))
	return RadiusMin + float64(code)/65535*(RadiusMax-RadiusMin)
}

// Generate builds the six-grid cube-sphere field from the world seed.
// Deterministic: same seed → bit-identical field.
func Generate(seed uint64) *Field {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	craters := placeCraters(rng, lm, seed)

	// Step 7 (spawn plain) needs the spawn-point radius; compute it once.
	// The disc forces r = rSpawn inside spawnFlatRadius, so rSpawn is the
	// pre-plain value at the spawn direction itself.
	var clamped int
	rSpawn := quantise(radiusAt(SpawnDir, seed, craters, lm), &clamped)

	f := &Field{Seed: seed}
	for i := range lm {
		f.Landmarks = append(f.Landmarks, LandmarkPos{Name: lm[i].name, Dir: lm[i].dir})
	}
	for i := range craters {
		f.Craters = append(f.Craters, CraterPos{Dir: craters[i].dir, Radius: craters[i].rimRad * PlanetRadius})
	}
	discAng := spawnFlatRadius / PlanetRadius
	bandAng := (spawnFlatRadius + spawnFlatBlend) / PlanetRadius
	for face := range NumFaces {
		for row := range FaceGrid {
			v := 2*float64(row)/(FaceGrid-1) - 1
			for col := range FaceGrid {
				u := 2*float64(col)/(FaceGrid-1) - 1
				d := DirOf(face, u, v)
				r := radiusAt(d, seed, craters, lm)

				// Spawn plain, applied last so nothing overrides it:
				// force flat to the spawn radius inside the disc, blending
				// out over the band.
				if ang := angularDist(d, SpawnDir); ang < bandAng {
					w := smoothstep(discAng, bandAng, ang)
					r = rSpawn + (r-rSpawn)*w
				}

				f.Radii[face][row*FaceGrid+col] = quantise(r, &clamped)
			}
		}
	}
	f.Clamped = clamped
	return f
}

func clampRadius(r float64) float64 {
	if r < RadiusMin {
		return RadiusMin
	}
	if r > RadiusMax {
		return RadiusMax
	}
	return r
}

// ---- constraint report (GDD "Must hold") ----

// Report measures the binding shape constraints against the generated
// field. qa verifies these, not the recipe.
type Report struct {
	WalkableFraction float64 // fraction of samples with slope ≤ max_slope
	MinR, MaxR       float64
	SpawnFlatErr     float64 // max radius deviation inside the spawn disc
	Clamped          int     // samples clamped to [RadiusMin, RadiusMax]
	LandmarkMinSep   float64 // radians, minimum pairwise landmark separation
	SeamMaxErr       float64 // max radius mismatch across shared face edges
}

// Report walks the field and measures the GDD "Must hold" constraints.
func (f *Field) Report() Report {
	var rep Report
	rep.Clamped = f.Clamped
	n := 0
	walkable := 0
	minR, maxR := math.Inf(1), math.Inf(-1)
	discAng := spawnFlatRadius / PlanetRadius
	rSpawn := f.SampleRadius(SpawnDir)
	for face := range NumFaces {
		for row := range FaceGrid {
			v := 2*float64(row)/(FaceGrid-1) - 1
			for col := range FaceGrid {
				u := 2*float64(col)/(FaceGrid-1) - 1
				d := DirOf(face, u, v)
				r := f.Radii[face][row*FaceGrid+col]
				n++
				if r < minR {
					minR = r
				}
				if r > maxR {
					maxR = r
				}
				if f.Walkable(d) {
					walkable++
				}
				if angularDist(d, SpawnDir) <= discAng {
					if err := math.Abs(r - rSpawn); err > rep.SpawnFlatErr {
						rep.SpawnFlatErr = err
					}
				}
			}
		}
	}
	rep.WalkableFraction = float64(walkable) / float64(n)
	rep.MinR = minR
	rep.MaxR = maxR
	rep.SeamMaxErr = f.seamError()
	if len(f.Landmarks) > 1 {
		sep := math.Inf(1)
		for i := range f.Landmarks {
			for j := i + 1; j < len(f.Landmarks); j++ {
				if a := angularDist(f.Landmarks[i].Dir, f.Landmarks[j].Dir); a < sep {
					sep = a
				}
			}
		}
		rep.LandmarkMinSep = sep
	}
	return rep
}

// seamError checks that shared edges duplicated between adjacent faces
// carry identical values (a mismatch is a visible crack and a fall-through
// spot). With 3D-noise generation this is 0 by construction; this catches a
// regression if the generator ever changes.
func (f *Field) seamError() float64 {
	// Every grid sample on a face edge is also a grid sample on a
	// neighbouring face. Resample it with the shared FaceOf tie-break and
	// compare against the stored value: equal faces agree trivially,
	// different faces get the cross-face check.
	var maxErr float64
	for face := range NumFaces {
		for row := range FaceGrid {
			v := 2*float64(row)/(FaceGrid-1) - 1
			for col := range FaceGrid {
				u := 2*float64(col)/(FaceGrid-1) - 1
				if u > -1 && u < 1 && v > -1 && v < 1 {
					continue // interior: no neighbour to agree with
				}
				d := DirOf(face, u, v)
				if err := math.Abs(f.SampleRadius(d) - f.Radii[face][row*FaceGrid+col]); err > maxErr {
					maxErr = err
				}
			}
		}
	}
	return maxErr
}
