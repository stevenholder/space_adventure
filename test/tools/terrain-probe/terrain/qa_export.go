package terrain
import (
	"math"
	"math/rand/v2"
)

// QA types expose the unexported placement results for QA tooling.
// This file exists only in the QA copy.

type QALandmark struct {
	Name      string
	Dir       Vec
	Footprint float64
}

type QACrater struct {
	Dir    Vec
	RimRad float64
	Depth  float64
	RimH   float64
}

// QAPlacement re-runs the deterministic landmark/crater placement sequence
// exactly as Generate does (same RNG, same call order).
func QAPlacement(seed uint64) (lm []QALandmark, craters []QACrater) {
	rng := rand.New(rand.NewPCG(seed, 0))
	lms := makeLandmarks(rng)
	crs := placeCraters(rng, lms, seed)
	for _, l := range lms {
		lm = append(lm, QALandmark{Name: l.name, Dir: l.dir, Footprint: l.footprint})
	}
	for _, c := range crs {
		craters = append(craters, QACrater{Dir: c.dir, RimRad: c.rimRad, Depth: c.depth, RimH: c.rimH})
	}
	return lm, craters
}

// QARadiusAt evaluates the product's radiusAt at a unit direction (same
// crater/landmark placement as Generate).
func QARadiusAt(seed uint64, d Vec) float64 {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	crs := placeCraters(rng, lm, seed)
	return radiusAt(d, seed, crs, lm)
}

// QARadiusNoCraters evaluates radiusAt with an empty crater list, consuming
// the identical RNG stream first, so the difference against QARadiusAt is the
// exact crater contribution.
func QARadiusNoCraters(seed uint64, d Vec) float64 {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	_ = placeCraters(rng, lm, seed)
	return radiusAt(d, seed, nil, lm)
}

// QACraterProfile exposes the unexported crater profile for QA checks.
func QACraterProfile(t, depth, rimH float64) float64 {
	return craterProfile(t, depth, rimH)
}

// QANodeDebug returns, for a unit direction d, the exact stored field value
// at the nearest lattice node, the fresh radiusAt at that node's direction,
// and the fresh radiusAt at d itself.
func QANodeDebug(seed uint64, d Vec) (nodeDir Vec, stored, freshNode, freshD float64) {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	crs := placeCraters(rng, lm, seed)
	face, u, v := FaceOf(d)
	c0 := int((u+1)/2*(FaceGrid-1)+0.5)
	r0 := int((v+1)/2*(FaceGrid-1)+0.5)
	if c0 > FaceGrid-1 {
		c0 = FaceGrid - 1
	}
	if r0 > FaceGrid-1 {
		r0 = FaceGrid - 1
	}
	nd := DirOf(face, 2*float64(c0)/float64(FaceGrid-1)-1, 2*float64(r0)/float64(FaceGrid-1)-1)
	stored = Generate(seed).Radii[face][r0*FaceGrid+c0]
	freshNode = radiusAt(nd, seed, crs, lm)
	freshD = radiusAt(d, seed, crs, lm)
	nodeDir = nd
	return
}

// QARadialProfile samples raw radiusAt (with craters) and no-crater radiusAt
// along a great-circle ray from dir, every stepM metres, for len steps.
func QARadialProfile(seed uint64, dir Vec, axis Vec, stepM float64, steps int) (withCr, noCr []float64) {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	crs := placeCraters(rng, lm, seed)
	up := Normalize(dir)
	k := axis
	dk := up.Dot(k)
	e1 := Normalize(k.Sub(up.Scale(dk)))
	withCr = make([]float64, 0, steps)
	noCr = make([]float64, 0, steps)
	for i := 0; i < steps; i++ {
		t := float64(i) * stepM / PlanetRadius
		ct, st := math.Cos(t), math.Sin(t)
		dd := Normalize(up.Scale(ct).Add(e1.Scale(st)))
		withCr = append(withCr, radiusAt(dd, seed, crs, lm))
		noCr = append(noCr, radiusAt(dd, seed, nil, lm))
	}
	return withCr, noCr
}

// QADecompose returns the layered radiusAt components (base, ridge, detail,
// craterSum; landmarks excluded) for QA diagnostics. Mirrors the product
// radiusAt body step-by-step.
func QADecompose(seed uint64, d Vec) (base, ridge, detail, cratersSum float64) {
	rng := rand.New(rand.NewPCG(seed, 0))
	lm := makeLandmarks(rng)
	crs := placeCraters(rng, lm, seed)
	baseN := fbm(d, baseFreq, baseOctaves, seed)
	mask := smoothstep(highlandThreshold-highlandWindow, highlandThreshold+highlandWindow, baseN)
	angSpawn := angularDist(d, SpawnDir)
	spawnMask := smoothstep(spawnSupIn, spawnSupOut, angSpawn)
	base = (baseN - 0.5) * 2 * baseAmp * spawnMask
	ridge = (2*ridged(d, ridgeFreq, ridgeOctaves, seed) - 1) * ridgeAmp * mask * spawnMask
	detailScale := plainDamp + (1 - plainDamp) * mask
	detail = (fbm(d, detailFreq, detailOctaves, seed+0xABCD)*2-1) * detailAmp * detailScale * spawnMask
	for i := range crs {
		c := &crs[i]
		t := angularDist(d, c.dir) / c.rimRad
		if t < 1.4 {
			cratersSum += craterProfile(t, c.depth, c.rimH)
		}
	}
	return
}

// QARadialStored samples the generated field's bilinear SampleRadius along a
// great-circle ray from dir, every stepM metres, for steps samples.
func QARadialStored(seed uint64, dir Vec, axis Vec, stepM float64, steps int) []float64 {
	f := Generate(seed)
	up := Normalize(dir)
	k := Normalize(axis)
	dk := up.Dot(k)
	e1 := Normalize(k.Sub(up.Scale(dk)))
	out := make([]float64, 0, steps)
	for i := range steps {
		t := float64(i) * stepM / PlanetRadius
		ct, st := math.Cos(t), math.Sin(t)
		dd := Normalize(up.Scale(ct).Add(e1.Scale(st)))
		out = append(out, f.SampleRadius(dd))
	}
	return out
}
