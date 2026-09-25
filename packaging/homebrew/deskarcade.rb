# Homebrew cask for Desk Arcade, stamped for a release by packaging/Update-PackageManifests.ps1.
# Published in the tap https://github.com/BokhodirUrinboev/homebrew-tap (Casks/deskarcade.rb): see docs/RELEASING.md.
cask "deskarcade" do
  arch arm: "arm64", intel: "x64"

  version "1.8.0"
  sha256 arm:   "8fc3720d8fdd6cb46cb02a5b8bd839dc2713bf49340aaf2a702cd0f186b1c193",
         intel: "ccfe3b0e0d6dec16765764ad113672d2eadcf401a00044be629c888b72976d17"

  url "https://github.com/BokhodirUrinboev/DeskArcade/releases/download/v#{version}/DeskArcade-#{version}-macos-#{arch}.zip"
  name "Desk Arcade"
  desc "Mini-games that play on top of your desktop in a transparent overlay"
  homepage "https://github.com/BokhodirUrinboev/DeskArcade"

  livecheck do
    url :url
    strategy :github_latest
  end

  depends_on macos: ">= :sonoma"

  app "DeskArcade.app"

  uninstall launchctl: "com.imperiumgames.deskarcade",
            quit:      "com.imperiumgames.deskarcade"

  caveats <<~EOS
    Desk Arcade is ad-hoc signed and not notarized. If macOS refuses to open it, right-click the app
    in Applications and choose Open, or run:
      xattr -dr com.apple.quarantine /Applications/DeskArcade.app
  EOS

  zap trash: [
    "~/.config/DeskArcade",
    "~/Library/LaunchAgents/com.imperiumgames.deskarcade.plist",
  ]
end
