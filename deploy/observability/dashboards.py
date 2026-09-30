#!/usr/bin/env python3
"""The Space Adventure Grafana dashboard, as code.

    python3 deploy/observability/dashboards.py          # JSON on stdout
    python3 deploy/observability/dashboards.py --push   # upsert into Grafana

--push needs GRAFANA_TOKEN (a service-account token with Editor) and takes
GRAFANA_URL (default: the LGTM box). Idempotent: same uid, overwrite=true.

Generated rather than checked in as JSON because every panel repeats the
same two things — the env template filter and the fixed kind/prod colors —
and a 400-line JSON blob hides a one-line change. The `env` variable is the
whole point of the labels Alloy stamps: pick kind, prod, or both overlaid.
"""

import json
import os
import sys
import urllib.request

DS = {"type": "prometheus", "uid": "prometheus"}
ENV = 'env=~"$env"'
SERVER = f'job="server", {ENV}'

# One color per environment, on every panel, whatever the series count.
# Blue/orange: the classic CVD-safe pair.
ENV_COLORS = {"kind": "blue", "prod": "orange"}


def env_overrides(dashed=None):
    """Color by env, never by series order; dashed for a secondary measure."""
    o = [
        {
            "matcher": {"id": "byRegexp", "options": f"/{env}/"},
            "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": c}}],
        }
        for env, c in ENV_COLORS.items()
    ]
    if dashed:
        o.append(
            {
                "matcher": {"id": "byRegexp", "options": dashed},
                "properties": [{"id": "custom.lineStyle", "value": {"fill": "dash", "dash": [10, 10]}}],
            }
        )
    return o


def target(expr, legend, ref="A"):
    return {"datasource": DS, "expr": expr, "legendFormat": legend, "refId": ref}


def timeseries(title, targets, x, y, w=12, h=8, unit=None, thresholds=None, dashed=None, desc=""):
    p = {
        "type": "timeseries",
        "title": title,
        "description": desc,
        "datasource": DS,
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "targets": [target(e, l, chr(65 + i)) for i, (e, l) in enumerate(targets)],
        "fieldConfig": {
            "defaults": {
                "custom": {"lineWidth": 2, "fillOpacity": 0, "pointSize": 4, "showPoints": "never"},
                "color": {"mode": "palette-classic"},
            },
            "overrides": env_overrides(dashed),
        },
        "options": {
            "legend": {"displayMode": "list", "placement": "bottom", "showLegend": True},
            "tooltip": {"mode": "multi", "sort": "desc"},
        },
    }
    if unit:
        p["fieldConfig"]["defaults"]["unit"] = unit
    if thresholds:
        # Drawn as lines on the plot: the budget is visible without reading a legend.
        p["fieldConfig"]["defaults"]["thresholds"] = {"mode": "absolute", "steps": thresholds}
        p["fieldConfig"]["defaults"]["custom"]["thresholdsStyle"] = {"mode": "line"}
    return p


def stat(title, expr, legend, x, y, w=6, h=4, unit=None, mappings=None, text_mode="value", desc=""):
    p = {
        "type": "stat",
        "title": title,
        "description": desc,
        "datasource": DS,
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "targets": [target(expr, legend)],
        "fieldConfig": {
            "defaults": {"color": {"mode": "fixed", "fixedColor": "text"}, "thresholds": {"mode": "absolute", "steps": [{"color": "text", "value": None}]}},
            "overrides": env_overrides(),
        },
        "options": {
            "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
            "textMode": text_mode,
            "colorMode": "value",
            "graphMode": "none",
            "justifyMode": "center",
        },
    }
    if unit:
        p["fieldConfig"]["defaults"]["unit"] = unit
    if mappings:
        p["fieldConfig"]["defaults"]["mappings"] = mappings
    return p


panels = [
    # Row 1: the four questions asked first.
    stat("Players online", f"sum by (env) (space_adventure_players_online{{{ENV}}})", "{{env}}", 0, 0,
         text_mode="value_and_name"),
    stat("Build", f"space_adventure_build_info{{{ENV}}}", "{{env}} · {{build}}", 6, 0, text_mode="name",
         desc="The git rev of server/, same as /version."),
    stat("Scrape", f'min by (env) (up{{{SERVER}}})', "{{env}}", 12, 0, text_mode="value_and_name",
         mappings=[{"type": "value", "options": {
             "0": {"text": "DOWN", "color": "red", "index": 0},
             "1": {"text": "UP", "color": "green", "index": 1}}}],
         desc="Alloy reaching the server's :9100. DOWN here and everything else is stale."),
    stat("Tick rate", f"sum by (env) (rate(space_adventure_tick_seconds_count{{{ENV}}}[1m]))", "{{env}}", 18, 0,
         unit="hertz", text_mode="value_and_name", desc="Should sit at 20 Hz. Below it the sim is falling behind."),
    # Row 2: the game.
    timeseries("Players online", [(f"sum by (env) (space_adventure_players_online{{{ENV}}})", "{{env}}")], 0, 4),
    timeseries(
        "Tick time (p50 / p99)",
        [
            (f"histogram_quantile(0.99, sum by (env, le) (rate(space_adventure_tick_seconds_bucket{{{ENV}}}[2m])))", "{{env}} p99"),
            (f"histogram_quantile(0.50, sum by (env, le) (rate(space_adventure_tick_seconds_bucket{{{ENV}}}[2m])))", "{{env}} p50"),
        ],
        12, 4, unit="s", dashed="/p50/",
        thresholds=[{"color": "transparent", "value": None}, {"color": "orange", "value": 0.005}, {"color": "red", "value": 0.05}],
        desc="Wall time of one sim tick. Orange line: the server's own 'slow tick' log threshold (5 ms). Red line: the whole 20 Hz budget (50 ms).",
    ),
    timeseries(
        "Ticks over 4 ms per minute",
        [(f'sum by (env) (increase(space_adventure_tick_seconds_count{{{ENV}}}[1m])) - sum by (env) (increase(space_adventure_tick_seconds_bucket{{le="0.004", {ENV}}}[1m]))', "{{env}}")],
        0, 12, desc="Count of slow-ish ticks. Zero is normal; a steady number is load, a spike is a stall.",
    ),
    timeseries(
        "Network",
        [
            (f"rate(process_network_transmit_bytes_total{{{SERVER}}}[2m])", "{{env}} out"),
            (f"rate(process_network_receive_bytes_total{{{SERVER}}}[2m])", "{{env}} in"),
        ],
        12, 12, unit="Bps", dashed="/ in$/", desc="Snapshots out, inputs in. Scales with players.",
    ),
    # Row 3: the process.
    timeseries(
        "Memory",
        [
            (f"process_resident_memory_bytes{{{SERVER}}}", "{{env}} rss"),
            (f"go_memstats_heap_inuse_bytes{{{SERVER}}}", "{{env}} heap"),
        ],
        0, 20, w=8, unit="bytes", dashed="/heap/", desc="The prod limit is 512Mi, kind's 128Mi.",
    ),
    timeseries("CPU", [(f"rate(process_cpu_seconds_total{{{SERVER}}}[2m])", "{{env}}")], 8, 20, w=8, unit="percentunit",
               desc="Of one core. The prod request is 100m."),
    timeseries("Goroutines", [(f"go_goroutines{{{SERVER}}}", "{{env}}")], 16, 20, w=8,
               desc="Two per connection plus the sim. Growth without players is a leak."),
]

dashboard = {
    "uid": "space-adventure",
    "title": "Space Adventure",
    "tags": ["space-adventure"],
    "timezone": "browser",
    "refresh": "30s",
    "time": {"from": "now-1h", "to": "now"},
    "schemaVersion": 39,
    "editable": True,
    "templating": {
        "list": [
            {
                "name": "env",
                "label": "env",
                "type": "query",
                "datasource": DS,
                "query": {"query": "label_values(space_adventure_build_info, env)", "refId": "env"},
                "refresh": 2,
                "multi": True,
                "includeAll": True,
                "allValue": ".*",
                "current": {"text": "All", "value": "$__all"},
                "sort": 1,
            }
        ]
    },
    "panels": [dict(p, id=i + 1) for i, p in enumerate(panels)],
}


def push():
    url = os.environ.get("GRAFANA_URL", "http://192.168.1.112:3000").rstrip("/")
    tok = os.environ.get("GRAFANA_TOKEN")
    if not tok:
        sys.exit("GRAFANA_TOKEN unset")
    body = json.dumps({"dashboard": dict(dashboard, id=None), "overwrite": True, "message": "dashboards.py"}).encode()
    req = urllib.request.Request(f"{url}/api/dashboards/db", data=body, method="POST",
                                 headers={"Authorization": f"Bearer {tok}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        out = json.load(r)
    print(f"{url}{out['url']}  (version {out['version']})")


if __name__ == "__main__":
    if "--push" in sys.argv[1:]:
        push()
    else:
        json.dump(dashboard, sys.stdout, indent=2)
        print()
