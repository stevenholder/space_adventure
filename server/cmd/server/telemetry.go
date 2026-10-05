package main

import (
	"context"
	"os"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

// initTracing installs the global OTLP tracer provider — only when
// OTEL_EXPORTER_OTLP_ENDPOINT is set (the manifests point it at the Alloy
// Service). Unset, nothing is installed: otel's global tracer stays the
// no-op, so tests and a bare `go run` pay nothing and dial nothing. The
// exporter reads the endpoint itself; an http:// scheme means plaintext.
func initTracing(ctx context.Context) (shutdown func(context.Context) error, err error) {
	if os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT") == "" {
		return func(context.Context) error { return nil }, nil
	}
	exp, err := otlptracegrpc.New(ctx)
	if err != nil {
		return nil, err
	}
	// No semconv schema URL on purpose: merging two resources with
	// different schema URLs is an error, and plain keys say the same thing.
	res, err := resource.New(ctx,
		resource.WithFromEnv(), // OTEL_RESOURCE_ATTRIBUTES, if anyone sets it
		resource.WithTelemetrySDK(),
		resource.WithAttributes(
			attribute.String("service.name", "space-adventure-server"),
			attribute.String("service.version", buildID),
			attribute.String("deployment.environment", os.Getenv("DEPLOY_ENV")),
		),
	)
	if err != nil {
		return nil, err
	}
	tp := sdktrace.NewTracerProvider(sdktrace.WithBatcher(exp), sdktrace.WithResource(res))
	otel.SetTracerProvider(tp)
	return tp.Shutdown, nil
}
