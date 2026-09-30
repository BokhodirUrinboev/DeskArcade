// Points the big download button at the right file for the visitor's system, from the latest release. Without
// JavaScript, or when GitHub doesn't answer, it stays a link to the releases page, which lists every file.
(function () {
  var button = document.getElementById('primary');
  var note = document.getElementById('primary-note');
  var ua = navigator.userAgent;
  var os = /Windows/.test(ua) ? 'windows' : /Mac OS X|Macintosh/.test(ua) ? 'mac' : /Linux|X11/.test(ua) && !/Android/.test(ua) ? 'linux' : null;
  if (!os) return;

  // Which release asset to offer, by name, and what the button says.
  var picks = {
    windows: { test: /^DeskArcade-Setup-[\d.]+-standalone\.exe$/, label: 'Download for Windows', note: 'The installer · per user, no admin rights' },
    linux: { test: /^deskarcade_[\d.]+_amd64\.deb$/, label: 'Download for Ubuntu and Debian', note: 'The .deb · an AppImage and the AUR are below' },
    mac: { test: /^DeskArcade-[\d.]+-macos-arm64\.zip$/, label: 'Download for macOS', note: 'Experimental · right-click → Open the first time' }
  };
  var pick = picks[os];
  button.textContent = pick.label;
  note.textContent = pick.note;

  fetch('https://api.github.com/repos/BokhodirUrinboev/DeskArcade/releases/latest')
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (release) {
      if (!release || !release.assets) return;
      var asset = release.assets.filter(function (a) { return pick.test.test(a.name); })[0];
      if (!asset) return;
      button.href = asset.browser_download_url;
      note.textContent = pick.note + ' · version ' + String(release.tag_name || '').replace(/^v/, '');
    })
    .catch(function () { /* keep the releases page */ });
})();
