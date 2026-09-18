# Homebrew cask for Desk Arcade, stamped for a release by packaging/Update-PackageManifests.ps1.
# Publishing it (a tap, or homebrew/cask) is a manual step: see docs/RELEASING.md.
cask "deskarcade" do
  arch arm: "arm64", intel: "x64"

  version "1.5.0"
  sha256 arm:   "5f3afd2a053098e6d34c49bd7186bf9cbe572f3eb1838770d093e74b9614d8ce",
         intel: "3862dffdf0d6be4d9b81d74aa4bf573d055136869ff817c887d97493cc620fa1"

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

  zap trash: [
    "~/.config/DeskArcade",
    "~/Library/LaunchAgents/com.imperiumgames.deskarcade.plist",
  ]
end
