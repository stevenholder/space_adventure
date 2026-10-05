package server

import "go.opentelemetry.io/otel"

// tracer is the global provider's: a no-op unless cmd/server installed the
// OTLP one (OTEL_EXPORTER_OTLP_ENDPOINT set). Spans are few on purpose --
// join, cmd, store calls, and slow ticks only; a 20 Hz tick is never traced.
var tracer = otel.Tracer("space-adventure/server")
