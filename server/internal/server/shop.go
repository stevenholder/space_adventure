// Buyback (Phase 13, docs/GDD.md "shop_sell"): a stack sold this session
// can be bought back at exactly what the shop paid, from any shop, until
// the list rolls it off or the connection ends.
package server

// buybackMax is how many sales the list remembers (WoW keeps 12).
const buybackMax = 12

// buybackEntry is one sale: the stack and the credits it fetched.
type buybackEntry struct {
	Item  string `json:"item"`
	Qty   int    `json:"qty"`
	Price int64  `json:"price"`
}

// pushBuyback records a sale, dropping the oldest past the cap. Under s.mu.
func (c *client) pushBuyback(e buybackEntry) {
	c.buyback = append(c.buyback, e)
	if len(c.buyback) > buybackMax {
		c.buyback = c.buyback[len(c.buyback)-buybackMax:]
	}
}

// popBuyback removes and returns the NEWEST entry for item. Under s.mu.
func (c *client) popBuyback(item string) (buybackEntry, bool) {
	for i := len(c.buyback) - 1; i >= 0; i-- {
		if c.buyback[i].Item == item {
			e := c.buyback[i]
			c.buyback = append(c.buyback[:i], c.buyback[i+1:]...)
			return e, true
		}
	}
	return buybackEntry{}, false
}
