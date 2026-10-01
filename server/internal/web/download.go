package web

// /download/windows and /download/linux: a stable link to the newest
// release's installer. GitHub's own releases/latest/download/... skips
// pre-releases, and deploy.yml publishes nothing else, so we look the
// asset up ourselves and redirect to its public download URL.

import (
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"sync"
	"time"
)

const releasesRepo = "stevenholder/space_adventure"

// The names deploy.yml's Velopack pack step publishes.
var downloadAssets = map[string]string{
	"windows": "SpaceAdventure-win-Setup.exe",
	"linux":   "SpaceAdventure.AppImage",
}

type ghAsset struct {
	Name        string `json:"name"`
	DownloadURL string `json:"browser_download_url"`
}

type ghRelease struct {
	Draft     bool      `json:"draft"`
	Published time.Time `json:"published_at"`
	Assets    []ghAsset `json:"assets"`
}

type downloads struct {
	api  string // https://api.github.com, a fake in tests
	http *http.Client

	mu       sync.Mutex
	releases []ghRelease
	fetched  time.Time
}

func newDownloads() *downloads {
	return &downloads{api: "https://api.github.com", http: &http.Client{Timeout: 10 * time.Second}}
}

// list is the newest releases, cached five minutes: a visitor per click
// would burn the 60/h anonymous API budget fast.
func (d *downloads) list() ([]ghRelease, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.releases != nil && time.Since(d.fetched) < 5*time.Minute {
		return d.releases, nil
	}
	resp, err := d.http.Get(d.api + "/repos/" + releasesRepo + "/releases?per_page=20")
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, errors.New("github releases: " + resp.Status)
	}
	var rs []ghRelease
	if err := json.NewDecoder(resp.Body).Decode(&rs); err != nil {
		return nil, err
	}
	d.releases, d.fetched = rs, time.Now()
	return rs, nil
}

// resolve is where to send the visitor for the newest release carrying name.
func (d *downloads) resolve(name string) (string, error) {
	rs, err := d.list()
	if err != nil {
		return "", err
	}
	// Not GitHub's list order: it is not by date (same-day tags come back
	// shuffled), so take the latest published release that has the asset.
	var url string
	var newest time.Time
	for _, r := range rs {
		if r.Draft || !r.Published.After(newest) {
			continue
		}
		for _, a := range r.Assets {
			if a.Name == name {
				url, newest = a.DownloadURL, r.Published
			}
		}
	}
	if url == "" {
		return "", errors.New("no release carries " + name)
	}
	return url, nil
}

func (d *downloads) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	name, ok := downloadAssets[strings.TrimPrefix(r.URL.Path, "/download/")]
	if !ok {
		http.NotFound(w, r)
		return
	}
	url, err := d.resolve(name)
	if err != nil {
		http.Error(w, "download unavailable right now: "+err.Error(), http.StatusBadGateway)
		return
	}
	http.Redirect(w, r, url, http.StatusFound)
}
