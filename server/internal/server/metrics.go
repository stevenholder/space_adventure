package server

import "github.com/prometheus/client_golang/prometheus"

// tickSeconds is the one number that says whether the sim is keeping up:
// wall time of a full tick (step + encode + fan-out) against the 50 ms
// budget of 20 Hz. The "slow tick" log line in tick() fires past 5 ms;
// this is the same measurement as a distribution instead of an alarm.
//
// Registered on the default registry so cmd/server's /metrics sees it
// without plumbing; the registry is process-global and New() runs many
// times in tests, so the histogram is a package var, not a Server field.
var tickSeconds = prometheus.NewHistogram(prometheus.HistogramOpts{
	Name:    "space_adventure_tick_seconds",
	Help:    "Wall time of one simulation tick (step + encode + fan-out).",
	Buckets: prometheus.ExponentialBuckets(0.0005, 2, 10), // 0.5 ms .. 256 ms
})

// joinRefused counts hellos a strict server closed 1008 (Phase 16): a
// climbing count after a release is a client still joining as a guest.
var joinRefused = prometheus.NewCounter(prometheus.CounterOpts{
	Name: "space_adventure_join_refused_total",
	Help: "Hellos closed 1008 because the token owns no account character.",
})

func init() { prometheus.MustRegister(tickSeconds, joinRefused) }
