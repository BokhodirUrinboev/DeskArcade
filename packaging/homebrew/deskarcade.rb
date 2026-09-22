# Homebrew cask for Desk Arcade, stamped for a release by packaging/Update-PackageManifests.ps1.
# Published in the tap https://github.com/BokhodirUrinboev/homebrew-tap (Casks/deskarcade.rb): see docs/RELEASING.md.
cask "deskarcade" do
  arch arm: "arm64", intel: "x64"

  version "1.7.0"
  sha256 arm:   "ce4c16aeff0ab239f6a151ddab71f616f293ccffc6cfb5150c027e2b287536ac",
         intel: "4ce61d6f5e73ea5ced9480b66dbdda6f1821acc94dd16c68e85fa37671ae76d4"

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
