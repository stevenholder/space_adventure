package web

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDownloadRedirectsToNewestInstaller(t *testing.T) {
	gh := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/repos/" + releasesRepo + "/releases":
			// Not in date order, as GitHub really returns them: a draft, a
			// zip-era release, an older installer, then the newest one.
			w.Write([]byte(`[
			  {"draft":true,"published_at":"2026-10-03T00:00:00Z","assets":[{"name":"SpaceAdventure-win-Setup.exe","browser_download_url":"https://draft"}]},
			  {"published_at":"2026-10-02T12:00:00Z","assets":[{"name":"SpaceAdventure-v1-windows-x86_64.zip"}]},
			  {"published_at":"2026-10-01T23:00:00Z","assets":[{"name":"SpaceAdventure-win-Setup.exe","browser_download_url":"https://gh/old"}]},
			  {"published_at":"2026-10-01T23:40:00Z","assets":[
			    {"name":"SpaceAdventure-win-Setup.exe","browser_download_url":"https://gh/win"},
			    {"name":"SpaceAdventure.AppImage","browser_download_url":"https://gh/linux"}]}]`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer gh.Close()

	d := newDownloads()
	d.api = gh.URL
	check := func(path string, code int, loc string) {
		t.Helper()
		rec := httptest.NewRecorder()
		d.ServeHTTP(rec, httptest.NewRequest("GET", path, nil))
		if rec.Code != code || rec.Header().Get("Location") != loc {
			t.Fatalf("%s: got %d %q, want %d %q", path, rec.Code, rec.Header().Get("Location"), code, loc)
		}
	}
	check("/download/windows", 302, "https://gh/win")
	check("/download/linux", 302, "https://gh/linux")
	check("/download/mac", 404, "")
}
