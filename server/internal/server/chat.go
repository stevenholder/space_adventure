package server

import (
	"strings"
	"time"

	"space-adventure/server/internal/protocol"
)

// Chat limits (GDD "Chat (Phase 20)").
const (
	chatMaxBytes  = 200 // after the control strip and trim
	chatRateHz    = 1.0
	chatRateBurst = 3.0
)

// newChatRate is the per-connection chat bucket: burst 3, one line a
// second. Separate from the general cmd bucket, so chatting never costs a
// shop cmd and shop cmds never silence chat.
func newChatRate(now time.Time) *cmdRate {
	return &cmdRate{tokens: chatRateBurst, last: now, hz: chatRateHz, burst: chatRateBurst}
}

// chatCmd is OpChat: {"text": "..."}. Order follows handleCmd: the bucket
// first (an over-budget line is never parsed), then the body, then the
// rules. An accepted line goes to every connected client, the speaker
// included, as event 0x0011 with data = name NUL text. Nothing is stored;
// only chatLines counts it.
func (s *Server) chatCmd(c *client, req protocol.Cmd) protocol.CmdResult {
	res := protocol.CmdResult{Seq: req.Seq, Opcode: req.Opcode}
	if !c.chatRate.allow(time.Now()) {
		res.Status = protocol.StatusRateLimited
		return res
	}
	var body struct {
		Text string `json:"text"`
	}
	if len(req.Data) > protocol.MaxCmdBody || !decodeStrict(req.Data, &body) {
		res.Status = protocol.StatusMalformed
		return res
	}
	text := strings.TrimSpace(stripControls(body.Text))
	if text == "" {
		res.Status = protocol.StatusRefused
		res.Data = encodeJSON(map[string]string{"reason": "empty"})
		return res
	}
	if len(text) > chatMaxBytes {
		res.Status = protocol.StatusMalformed
		return res
	}
	f := chatFrame(c.entity.ID, c.entity.Name, text)
	s.mu.Lock()
	s.broadcast(f)
	s.mu.Unlock()
	chatLines.Inc()
	res.Status = protocol.StatusOK
	res.Data = []byte("{}")
	return res
}

// chatFrame renders the `chat` event: entity_id is the speaker, data is
// the speaker's name, a NUL, then the text (both UTF-8, neither can hold a
// NUL: both went through stripControls).
func chatFrame(entityID uint32, name, text string) []byte {
	return protocol.EncodeEvent(protocol.Event{
		EntityID: entityID,
		EventID:  protocol.EventChat,
		Data:     []byte(name + "\x00" + text),
	})
}
