package router

import (
	"bufio"
	"log"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Registry resolves a lobby code to its backend UDP address by reading the
// file registry that launch-lobby.sh writes (/tmp/sf-lobbies/<CODE>.conf, with
// key=value lines incl. code=, port=, pid=). Backends are loopback, so the
// address is 127.0.0.1:<port>. Entries whose pid is dead are dropped — same
// liveness rule serve-lobbies.py uses. Results are cached and refreshed on a
// short TTL so the hot path doesn't stat the directory per datagram.
type Registry struct {
	dir string
	ttl time.Duration

	mu        sync.Mutex
	byCode    map[string]*net.UDPAddr
	capByCode map[string]byte // max players per lobby (.conf capacity=, default 4)
	loadedAt  time.Time
}

// defaultCapacity is a stock Stick Fight lobby (4 players).
const defaultCapacity = 4

// NewRegistry returns a registry reading dir (e.g. /tmp/sf-lobbies) with the
// given cache TTL (~2s is plenty; lobbies come and go on human timescales).
func NewRegistry(dir string, ttl time.Duration) *Registry {
	return &Registry{dir: dir, ttl: ttl, byCode: map[string]*net.UDPAddr{}, capByCode: map[string]byte{}}
}

// Capacity returns the lobby's max player count from its .conf (capacity=,
// 1..4), or 4 when unset/unknown. Read on the cold LIST path.
func (r *Registry) Capacity(code string) byte {
	code = strings.ToUpper(strings.TrimSpace(code))
	r.refreshIfStale()
	r.mu.Lock()
	defer r.mu.Unlock()
	if c, ok := r.capByCode[code]; ok {
		return c
	}
	return defaultCapacity
}

// PlayersStale is how old a lobby's .players report may be before the router
// stops trusting it (the lobby rewrites it at least every 6 s).
const PlayersStale = 15 * time.Second

// Players returns the player count the lobby itself reports in
// <dir>/<CODE>.players, or ok=false if there is no report or it is stale.
func (r *Registry) Players(code string) (int, bool) {
	code = strings.ToUpper(strings.TrimSpace(code))
	if code == "" || strings.ContainsAny(code, "/\\.") {
		return 0, false
	}
	path := filepath.Join(r.dir, code+".players")
	fi, err := os.Stat(path)
	if err != nil || time.Since(fi.ModTime()) > PlayersStale {
		return 0, false
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return 0, false
	}
	n, err := strconv.Atoi(strings.TrimSpace(string(b)))
	if err != nil || n < 0 || n > 255 {
		return 0, false
	}
	return n, true
}

// Lookup returns the backend address for a lobby code (case-insensitive). It
// serves from the in-memory cache (kept warm by StartRefresh in production) and
// only scans the filesystem as a lazy fallback when the cache is stale — and
// never while holding the lock, so the router's hot path is not blocked on I/O.
func (r *Registry) Lookup(code string) (*net.UDPAddr, bool) {
	code = strings.ToUpper(strings.TrimSpace(code))
	r.refreshIfStale()
	r.mu.Lock()
	addr, ok := r.byCode[code]
	r.mu.Unlock()
	return addr, ok
}

// Codes returns the currently-known live lobby codes (for /router/stats / logs).
func (r *Registry) Codes() []string {
	r.refreshIfStale()
	r.mu.Lock()
	defer r.mu.Unlock()
	out := make([]string, 0, len(r.byCode))
	for c := range r.byCode {
		out = append(out, c)
	}
	return out
}

// refreshIfStale reloads the registry if the cache has aged past the TTL. This
// is the lazy fallback; in production StartRefresh keeps the cache warm so this
// rarely scans (and the scan, when it happens, holds no lock).
func (r *Registry) refreshIfStale() {
	r.mu.Lock()
	stale := time.Since(r.loadedAt) > r.ttl
	r.mu.Unlock()
	if stale {
		r.reload()
	}
}

// StartRefresh warms the cache immediately, then rescans every TTL in the
// background so Lookup/Codes serve from an in-memory map. The router holds its
// own mutex across Lookup, so keeping filesystem I/O out of that path (here,
// not there) is what stops a directory scan from stalling packet relay. Runs
// until stop is closed.
func (r *Registry) StartRefresh(stop <-chan struct{}) {
	r.reload()
	go func() {
		t := time.NewTicker(r.ttl)
		defer t.Stop()
		for {
			select {
			case <-stop:
				return
			case <-t.C:
				r.reload()
			}
		}
	}()
}

// reload rescans the dir WITHOUT holding r.mu (the scan does blocking I/O), then
// swaps the result in under a brief lock.
func (r *Registry) reload() {
	next, caps, replace := r.scan()
	r.mu.Lock()
	if replace {
		r.byCode = next
		r.capByCode = caps
	}
	r.loadedAt = time.Now()
	r.mu.Unlock()
}

// scan reads the registry dir and returns the fresh code→addr map. replace is
// false when a transient (non-ENOENT) error occurred and the caller should keep
// the previously-cached map, so a momentary FS hiccup doesn't drop every live
// client. A genuinely-missing dir (ENOENT) returns an empty map with
// replace=true (no lobbies). Holds no locks; safe to run concurrently with Lookup.
func (r *Registry) scan() (next map[string]*net.UDPAddr, caps map[string]byte, replace bool) {
	entries, err := os.ReadDir(r.dir)
	if err != nil {
		if os.IsNotExist(err) {
			return map[string]*net.UDPAddr{}, map[string]byte{}, true // dir gone → no lobbies
		}
		log.Printf("[registry] scan %s failed, keeping cached lobbies: %v", r.dir, err)
		return nil, nil, false
	}
	next = make(map[string]*net.UDPAddr)
	caps = make(map[string]byte)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".conf") {
			continue
		}
		code, port, pid, capacity, ok := parseConf(filepath.Join(r.dir, e.Name()))
		if !ok || !pidAlive(pid) {
			continue
		}
		next[strings.ToUpper(code)] = &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port}
		caps[strings.ToUpper(code)] = capacity
	}
	return next, caps, true
}

// parseConf reads a launch-lobby.sh .conf and returns code, port, pid and
// capacity (capacity= clamped to 1..4; default 4 when absent or invalid).
func parseConf(path string) (code string, port, pid int, capacity byte, ok bool) {
	capacity = defaultCapacity
	f, err := os.Open(path)
	if err != nil {
		return "", 0, 0, 0, false
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := sc.Text()
		i := strings.IndexByte(line, '=')
		if i < 0 {
			continue
		}
		k, v := line[:i], strings.TrimSpace(line[i+1:])
		switch k {
		case "code":
			code = v
		case "port":
			port, _ = strconv.Atoi(v)
		case "pid":
			pid, _ = strconv.Atoi(v)
		case "capacity":
			if n, err := strconv.Atoi(v); err == nil && n >= 1 && n <= defaultCapacity {
				capacity = byte(n)
			}
		}
	}
	if code == "" || port <= 0 || port > 65535 || pid <= 0 {
		return "", 0, 0, 0, false
	}
	return code, port, pid, capacity, true
}

// pidAlive reports whether pid is a live process. signal 0 probes existence:
// nil → alive; EPERM → exists but not ours (still alive); ESRCH → gone.
func pidAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	if err == nil {
		return true
	}
	return err == syscall.EPERM
}
