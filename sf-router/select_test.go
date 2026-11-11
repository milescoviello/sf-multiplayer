package router

import (
	"encoding/binary"
	"testing"
)

// buildSelect constructs a SELECT (or LEAVE) control datagram for tests and the
// client implementations to mirror.
func buildSelect(op byte, code string, nonce uint32) []byte {
	out := make([]byte, 0, minControlLen+len(code))
	out = append(out, selectMagic...)
	out = append(out, op, byte(len(code)))
	out = append(out, []byte(code)...)
	var n [4]byte
	binary.LittleEndian.PutUint32(n[:], nonce)
	out = append(out, n[:]...)
	return out
}

func TestControlRoundtrip(t *testing.T) {
	pkt := buildSelect(opSelect, "AB12", 0xDEADBEEF)
	if !isControl(pkt) {
		t.Fatal("isControl = false for a valid SELECT")
	}
	op, code, nonce, ok := parseControl(pkt)
	if !ok || op != opSelect || code != "AB12" || nonce != 0xDEADBEEF {
		t.Fatalf("parseControl = (%#x,%q,%#x,%v), want (opSelect,AB12,DEADBEEF,true)", op, code, nonce, ok)
	}
}

// TestGameTrafficNotControl ensures a Stick Fight datagram — [u32 ts][u8 msgType]
// [...] — is never mistaken for a control packet, across all valid msgTypes.
func TestGameTrafficNotControl(t *testing.T) {
	for msgType := 0; msgType <= 46; msgType++ {
		pkt := make([]byte, 14)
		binary.LittleEndian.PutUint32(pkt[0:4], 1769000000) // a 2026-era ts
		pkt[4] = byte(msgType)
		if isControl(pkt) {
			t.Errorf("game datagram with msgType=%d misclassified as control", msgType)
		}
	}
	// A datagram whose first bytes happen to collide on length but not magic.
	if isControl(make([]byte, 32)) {
		t.Error("all-zero datagram misclassified as control")
	}
}

func TestParseControlRejectsShort(t *testing.T) {
	if _, _, _, ok := parseControl(selectMagic); ok {
		t.Error("parseControl accepted a too-short datagram")
	}
	// codeLen says 8 but no code/nonce bytes follow.
	bad := append(append([]byte{}, selectMagic...), opSelect, 8)
	if _, _, _, ok := parseControl(bad); ok {
		t.Error("parseControl accepted a truncated code")
	}
}

func TestAckRoundtrip(t *testing.T) {
	ack := buildAck(0x01020304, ackNoSuchCode)
	if !isControl(ack) {
		t.Fatal("ACK not recognized as control framing")
	}
	status, nonce, ok := parseAck(ack)
	if !ok || nonce != 0x01020304 || status != ackNoSuchCode {
		t.Fatalf("parseAck = (status=%#x, nonce=%#x, ok=%v), want (ackNoSuchCode, 0x01020304, true)", status, nonce, ok)
	}
}

func TestDiagText(t *testing.T) {
	pkt := append(buildSelect(opDiag, "MAIN", 7), []byte("join stalled\n\x01ok")...)
	op, code, _, ok := parseControl(pkt)
	if !ok || op != opDiag || code != "MAIN" {
		t.Fatalf("parseControl = (%#x,%q,%v), want (opDiag,MAIN,true)", op, code, ok)
	}
	if got := diagText(pkt); got != "join stalled??ok" {
		t.Errorf("diagText = %q, want %q", got, "join stalled??ok")
	}
	if got := diagText(buildSelect(opDiag, "", 1)); got != "" {
		t.Errorf("diagText of an empty tail = %q, want empty", got)
	}
	long := append(buildSelect(opDiag, "", 1), make([]byte, maxDiagText+50)...)
	if got := diagText(long); len(got) != maxDiagText {
		t.Errorf("diagText length = %d, want %d", len(got), maxDiagText)
	}
}

func TestAllowDiagRateLimit(t *testing.T) {
	r := &Router{}
	for i := 0; i < diagPerWindow; i++ {
		if !r.allowDiag("1.2.3.4") {
			t.Fatalf("line %d refused, want allowed", i+1)
		}
	}
	if r.allowDiag("1.2.3.4") {
		t.Error("line past the per-IP limit allowed")
	}
	if !r.allowDiag("5.6.7.8") {
		t.Error("another IP refused")
	}
}
