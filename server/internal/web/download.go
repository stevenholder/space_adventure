package web

// /download/windows and /download/linux: a stable link to the newest
// release's installer. GitHub's own releases/latest/download/... skips
// pre-releases, and deploy.yml publishes nothing else, so we look the
// asset up ourselves and redirect.
//
// The repo is private: anonymous asset links 404. With SA_RELEASES_TOKEN
// (read access to contents) the asset API hands back a short-lived signed
// URL and the visitor is sent there — the bytes never pass through us.
// Without a token the public browser_download_url is used, which is right
// the day the repo goes public.

import (
	"encoding/json"
	"errors"
	"net/http"
	"os"
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
	URL         string `json:"url"`
	DownloadURL string `json:"browser_download_url"`
}

type ghRelease struct {
	Draft  bool      `json:"draft"`
	Assets []ghAsset `json:"assets"`
}

type downloads struct {
	api   string // https://api.github.com, a fake in tests
	token string
	http  *http.Client

	mu       sync.Mutex
	releases []ghRelease
	fetched  time.Time
}

func newDownloads() *downloads {
	return &downloads{
		api:   "https://api.github.com",
		token: os.Getenv("SA_RELEASES_TOKEN"),
		http: &http.Client{
			Timeout: 10 * time.Second,
			// The asset API answers with a redirect to the signed URL; we
			// want that Location, not the file.
			CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
		},
	}
}

func (d *downloads) get(url, accept string) (*http.Response, error) {
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", accept)
	if d.token != "" {
		req.Header.Set("Authorization", "Bearer "+d.token)
	}
	return d.http.Do(req)
}

// list is the newest releases, cached five minutes: a visitor per click
// would burn the 60/h anonymous API budget fast.
func (d *downloads) list() ([]ghRelease, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.releases != nil && time.Since(d.fetched) < 5*time.Minute {
		return d.releases, nil
	}
	resp, err := d.get(d.api+"/repos/"+releasesRepo+"/releases?per_page=20", "application/vnd.github+json")
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
	for _, r := range rs { // GitHub lists newest first
		if r.Draft {
			continue
		}
		for _, a := range r.Assets {
			if a.Name != name {
				continue
			}
			if d.token == "" {
				return a.DownloadURL, nil
			}
			resp, err := d.get(a.URL, "application/octet-stream")
			if err != nil {
				return "", err
			}
			resp.Body.Close()
			if loc := resp.Header.Get("Location"); resp.StatusCode/100 == 3 && loc != "" {
				return loc, nil
			}
			return "", errors.New("github asset: " + resp.Status)
		}
	}
	return "", errors.New("no release carries " + name)
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
