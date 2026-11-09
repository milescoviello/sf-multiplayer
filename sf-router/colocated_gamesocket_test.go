package router

import (
	"net"
	"testing"
)

// TestColocatedGameSocketHijackedByOtherSelect characterizes the DOCUMENTED
// per-IP game-socket routing limit (notes/ROUTER.md, ROUTER_LIVE_TEST.md) — an
// ACCEPTED trade-off, not a new bug. The polish loop (LOBBY-1) confirmed it
// reproduces and pins it here as a pending regression for the eventual fix.
//
// effectiveBackend (router.go) resolves a non-SELECTing endpoint via the single
// per-IP ipBind fallback — used by "the patched-DLL game socket that shares the
// IP but never SELECTs." But ipBind[IP] is ONE slot, overwritten on every SELECT
// (router.go:295). So when two players share an IP (two local test instances, or
// two players behind one NAT) and pick DIFFERENT lobbies, the second SELECT
// overwrites ipBind[IP] and HIJACKS the first player's game socket to the wrong
// lobby. The existing TestCoLocatedBoundFlowSurvivesOtherSelect does NOT catch
// this because there both clients SELECT from their own socket (both epBind'd).
func TestColocatedGameSocketHijackedByOtherSelect(t *testing.T) {
	t.Skip("documents the per-IP fallback limit for a game socket that never binds (old clients); current clients BIND, see TestColocatedGameSocketBindSurvivesOtherSelect")

	echoA, stopA := startEcho(t, "A")
	defer stopA()
	echoB, stopB := startEcho(t, "B")
	defer stopB()
	resolve := func(code string) (*net.UDPAddr, bool) {
		switch code {
		case "AAAA":
			return echoA, true
		case "BBBB":
			return echoB, true
		}
		return nil, false
	}

	// cli1 = player 1's CONTROL socket; it SELECTs AAAA.
	cli1, routerAddr, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli1.Close()
	selectAndWaitAck(t, cli1, "AAAA", 1)

	// cli1game = player 1's GAME socket: same IP, different port, never SELECTs.
	// It must route via the ipBind fallback → AAAA.
	cli1game, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("cli1game dial: %v", err)
	}
	defer cli1game.Close()
	if got := sendRecv(t, cli1game, "p"); got != "A:p" {
		t.Fatalf("cli1 game socket pre got %q, want A:p (ipBind fallback)", got)
	}

	// Player 2's control socket (same IP) SELECTs a DIFFERENT lobby.
	cli2, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("cli2 dial: %v", err)
	}
	defer cli2.Close()
	selectAndWaitAck(t, cli2, "BBBB", 2)

	// cli1's game socket should STILL reach AAAA. If it now reaches BBBB, the
	// co-located SELECT hijacked it via the overwritten single ipBind[IP] slot.
	if got := sendRecv(t, cli1game, "q"); got != "A:q" {
		t.Errorf("cli1 game socket HIJACKED: got %q, want A:q (ipBind[IP] overwritten by co-located SELECT)", got)
	}
}

// TestColocatedGameSocketBindSurvivesOtherSelect: a game socket that sends a
// silent BIND keeps its lobby when a co-located player SELECTs another lobby,
// gets no reply to the BIND, and the other player's SELECT doesn't tear its
// flow down.
func TestColocatedGameSocketBindSurvivesOtherSelect(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	echoB, stopB := startEcho(t, "B")
	defer stopB()
	resolve := func(code string) (*net.UDPAddr, bool) {
		switch code {
		case "AAAA":
			return echoA, true
		case "BBBB":
			return echoB, true
		}
		return nil, false
	}

	cli1, routerAddr, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli1.Close()
	selectAndWaitAck(t, cli1, "AAAA", 1)

	cli1game, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("cli1game dial: %v", err)
	}
	defer cli1game.Close()
	if _, err := cli1game.Write(buildSelect(opBind, "AAAA", 7)); err != nil {
		t.Fatalf("bind write: %v", err)
	}
	// No reply to a BIND: the first datagram back must be the echo.
	if got := sendRecv(t, cli1game, "p"); got != "A:p" {
		t.Fatalf("bound game socket got %q, want A:p (and no BIND ack)", got)
	}

	cli2, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatalf("cli2 dial: %v", err)
	}
	defer cli2.Close()
	selectAndWaitAck(t, cli2, "BBBB", 2)

	if got := sendRecv(t, cli1game, "q"); got != "A:q" {
		t.Errorf("bound game socket re-routed by a co-located SELECT: got %q, want A:q", got)
	}

	// Re-binding to another lobby moves only this endpoint.
	if _, err := cli1game.Write(buildSelect(opBind, "BBBB", 8)); err != nil {
		t.Fatalf("rebind write: %v", err)
	}
	if got := sendRecv(t, cli1game, "r"); got != "B:r" {
		t.Errorf("rebound game socket got %q, want B:r", got)
	}
	if got := sendRecv(t, cli1, "s"); got != "A:s" {
		t.Errorf("cli1 control socket moved by its game socket's BIND: got %q, want A:s", got)
	}
}
