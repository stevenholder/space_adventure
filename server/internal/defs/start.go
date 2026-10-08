package defs

import (
	"fmt"
	"strconv"
	"strings"
)

// ApplyStartOverride replaces items.json's start_credits/start_items with
// spec, "credits=N,item=qty,...": a dev fleet's way back to a funded
// guest now that a new character starts with nothing (Phase 22). Every
// pair is checked before anything changes; credits left out stays 0.
func ApplyStartOverride(reg *Registry, spec string) error {
	var credits int64
	var items []struct {
		Item string `json:"item"`
		Qty  int    `json:"qty"`
	}
	for _, pair := range strings.Split(spec, ",") {
		pair = strings.TrimSpace(pair)
		if pair == "" {
			continue
		}
		k, v, ok := strings.Cut(pair, "=")
		k, v = strings.TrimSpace(k), strings.TrimSpace(v)
		n, err := strconv.ParseInt(v, 10, 64)
		if !ok || err != nil || n < 0 {
			return fmt.Errorf("start override: %q is not key=count", pair)
		}
		if k == "credits" {
			credits = n
			continue
		}
		it, known := reg.Items[k]
		if !known {
			return fmt.Errorf("start override: item %q unknown", k)
		}
		if n < 1 || n > int64(max(it.StackMax, 1)*max(reg.InvSlots, 1)) {
			return fmt.Errorf("start override: %s=%d does not fit a bag", k, n)
		}
		items = append(items, struct {
			Item string `json:"item"`
			Qty  int    `json:"qty"`
		}{k, int(n)})
	}
	reg.StartCredits, reg.StartItems = credits, items
	return nil
}
