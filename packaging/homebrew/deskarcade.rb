# Homebrew cask for Desk Arcade, stamped for a release by packaging/Update-PackageManifests.ps1.
# Publishing it (a tap, or homebrew/cask) is a manual step: see docs/RELEASING.md.
cask "deskarcade" do
  arch arm: "arm64", intel: "x64"

  version "1.6.0"
  sha256 arm:   "12311aee6d3b824796727f12ba0071dcaf3415202c9ae55259351dd7dd4c819f",
         intel: "88631bd590ca805deedddb106f47f35be94b4432650f5a79181b302f225de436"

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
