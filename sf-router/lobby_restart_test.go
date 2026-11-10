package router

import (
	"net"
	"testing"
	"time"
)

// A lobby restarting under connected players: while it is down, the client's
// datagrams make the kernel report ICMP port-unreachable on the flow's
// connected upstream socket, and the next Read returns ECONNREFUSED. The
// router used to treat that as "socket closed" and stop reading the flow, so
// the restarted lobby's replies were never relayed again: every player who was
// connected sat one-way (even a rejoin uses the same game socket) until they
// went quiet long enough for the flow to be reaped.
func TestFlowSurvivesBackendRestart(t *testing.T) {
	conn, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("backend listen: %v", err)
	}
	backend := conn.LocalAddr().(*net.UDPAddr)
	serve := func(c *net.UDPConn, prefix string) {
		buf := make([]byte, 2048)
		for {
			n, from, err := c.ReadFromUDP(buf)
			if err != nil {
				return
			}
			_, _ = c.WriteToUDP(append([]byte(prefix+":"), buf[:n]...), from)
		}
	}
	go serve(conn, "A")

	r, err := New("127.0.0.1:0", backend.String())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	go func() { _ = r.Run() }()
	defer r.Close()
	time.Sleep(50 * time.Millisecond)

	cli, err := net.DialUDP("udp", nil, r.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("client dial: %v", err)
	}
	defer cli.Close()
	buf := make([]byte, 2048)
	roundTrip := func(msg string, want string) bool {
		_, _ = cli.Write([]byte(msg))
		_ = cli.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
		n, err := cli.Read(buf)
		return err == nil && string(buf[:n]) == want
	}
	if !roundTrip("one", "A:one") {
		t.Fatal("no reply before the restart")
	}

	// Lobby down: the client keeps sending (ICMP refusals hit the flow).
	_ = conn.Close()
	for i := 0; i < 5; i++ {
		_, _ = cli.Write([]byte("while-down"))
		time.Sleep(20 * time.Millisecond)
	}

	// Lobby back on the same port.
	conn2, err := net.ListenUDP("udp", backend)
	if err != nil {
		t.Fatalf("backend relisten: %v", err)
	}
	defer conn2.Close()
	go serve(conn2, "B")

	ok := false
	for i := 0; i < 10 && !ok; i++ {
		ok = roundTrip("two", "B:two")
	}
	if !ok {
		t.Fatal("no reply from the restarted backend: the flow stopped relaying")
	}
}
