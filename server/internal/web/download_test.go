package web

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDownloadRedirectsToNewestInstaller(t *testing.T) {
	var gh *httptest.Server
	gh = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/repos/" + releasesRepo + "/releases":
			// Newest first: a draft, a zip-era release without installers, then the real one.
			w.Write([]byte(`[
			  {"draft":true,"assets":[{"name":"SpaceAdventure-win-Setup.exe","url":"x","browser_download_url":"https://draft"}]},
			  {"assets":[{"name":"SpaceAdventure-v1-windows-x86_64.zip"}]},
			  {"assets":[
			    {"name":"SpaceAdventure-win-Setup.exe","url":"` + gh.URL + `/asset/1","browser_download_url":"https://gh/win"},
			    {"name":"SpaceAdventure.AppImage","url":"` + gh.URL + `/asset/2","browser_download_url":"https://gh/linux"}]}]`))
		case "/asset/1":
			if r.Header.Get("Authorization") != "Bearer tok" || r.Header.Get("Accept") != "application/octet-stream" {
				http.Error(w, "nope", http.StatusNotFound)
				return
			}
			http.Redirect(w, r, "https://signed/win", http.StatusFound)
		default:
			http.NotFound(w, r)
		}
	}))
	defer gh.Close()

	d := newDownloads()
	d.api, d.token = gh.URL, ""
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

	d.token = "tok" // private repo: signed URL from the asset API
	check("/download/windows", 302, "https://signed/win")
}
