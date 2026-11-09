package router

import (
	"net"
	"testing"
	"time"
)

// RESTART is refused while the lobby has players (anyone can send it from the
// in-game browser) and honoured for an empty one.
func TestRestartRefusedWhileLobbyHasPlayers(t *testing.T) {
	echoA, stopA := startEcho(t, "A")
	defer stopA()
	resolve := func(code string) (*net.UDPAddr, bool) {
		if code == "AAAA" {
			return echoA, true
		}
		return nil, false
	}
	cli, routerAddr, r := newRoutingTest(t, resolve)
	defer r.Close()
	defer cli.Close()
	restarted := make(chan string, 4)
	r.SetController(func(op byte, code string) { restarted <- code })

	ctl := func(conn *net.UDPConn, nonce uint32) byte {
		t.Helper()
		if _, err := conn.Write(buildSelect(opRestart, "AAAA", nonce)); err != nil {
			t.Fatalf("write RESTART: %v", err)
		}
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		buf := make([]byte, 64)
		n, err := conn.Read(buf)
		if err != nil {
			t.Fatalf("read ctl ack: %v", err)
		}
		return buf[n-1-4]
	}

	// Empty lobby: accepted, the controller runs.
	other, err := net.DialUDP("udp", nil, routerAddr)
	if err != nil {
		t.Fatal(err)
	}
	defer other.Close()
	if st := ctl(other, 7); st != ctlAccepted {
		t.Fatalf("empty lobby RESTART: status %#x, want accepted", st)
	}
	select {
	case <-restarted:
	case <-time.After(2 * time.Second):
		t.Fatal("controller not called for an empty lobby")
	}

	// A player in the lobby: refused, the controller does not run.
	selectAndWaitAck(t, cli, "AAAA", 1)
	if got := sendRecv(t, cli, "p"); got != "A:p" {
		t.Fatalf("player traffic got %q", got)
	}
	if st := ctl(other, 8); st != ctlRejected {
		t.Fatalf("busy lobby RESTART: status %#x, want rejected", st)
	}
	select {
	case c := <-restarted:
		t.Fatalf("controller ran for a busy lobby (%s)", c)
	case <-time.After(300 * time.Millisecond):
	}
}
