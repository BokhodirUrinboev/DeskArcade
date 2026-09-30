# Good first issues (drafts)

Twelve small tasks for a first contribution, written as issues ready to paste into GitHub, for Hacktoberfest or any
other time. Each names the files to touch and how to check the change. Label them `good first issue` (and
`hacktoberfest` in October). Every one needs only the .NET 10 SDK: `dotnet run` starts the game from source and
`dotnet test tests/DeskArcade.Tests` runs the tests.

Some things hold for all of them:

- Text on screen goes through `L.T("English")` or `L.F("English {0}", x)`, and the Russian and Uzbek go in
  `i18n/ru.po` and `i18n/uz.po` (a native speaker's review is welcome; say in the pull request if you guessed).
  `UPDATE_POT=1 dotnet test` refreshes `i18n/deskarcade.pot` after adding text.
- `dotnet run -- --profile dev --demo --game <id> --snapshot shot.png` draws a game into a picture, to check a
  change's look without touching the mouse.
- The overlay never takes the keyboard or the focus, and nothing moves while idle: keep it that way.

---

## 1. A new theme

**Context.** Themes (tray → Theme) dress the scoreboard, boards, cards, pieces and the pet. There are thirteen.

**Task.** Add a fourteenth: pick a mood (Coffee, Terminal green, Pastel…), its colours, and one of the decorations
(`Decor`), or none.

**Files.** `src/Engine/Theme.cs` (a new `Theme` record next to the others and its place in `Choices`),
`tests/DeskArcade.Tests/ThemeTests.cs` (the count in `ThereAreThirteenThemesWithDistinctIdsAndNames`), and the
theme's name and mood in the two .po files.

**Check.** `ScoreboardTextReadsAgainstItsBackground` and the other theme tests pass; a snapshot of Chess and of
Solitaire in the new theme looks right.

## 2. Darts: Cricket

**Context.** Darts plays 501, double out (`DartsRules`).

**Task.** Add Cricket as a second mode: close 15 to 20 and the bull by hitting each three times, score on the numbers
you closed that the other side hasn't. A chip under the board switches modes, as the Solitaire chip does.

**Files.** `src/Games/DartsRules.cs` (a UI-free Cricket state and scoring), `src/Games/DartsGame.cs` (the chip and
the score sheet), `tests/DeskArcade.Tests/DartsTests.cs`.

**Check.** Tests for closing, scoring on closed numbers and the end of a leg; a demo run shows a Cricket sheet.

## 3. A second trick for a pet

**Context.** Every pet has a trick (right-click it) and, once grown up, a second one (`PetLife.SecondTrickFor`).

**Task.** Give one animal a third trick that it learns once it is "wise", drawn and animated in its own style (a
parrot that hangs upside down, a frog that catches a fly…).

**Files.** `src/Games/PetLife.cs` (when it is learnt), `src/Games/PetGame.cs` (the drawing and the timing, next to
the existing tricks), `tests/DeskArcade.Tests/PetLifeTests.cs`.

**Check.** A test that the trick comes with the wise stage and not before; a snapshot or a short screen recording.

## 4. Read and fix the Russian or Uzbek

**Context.** Much of the Russian and Uzbek text was written without a native speaker's read, so some of it is stiff
or wrong.

**Task.** Read one part of `i18n/ru.po` or `i18n/uz.po` (a game, the At work text, the menus) in the running game
(tray → Language) and fix what reads badly. Keep the `{0}` placeholders, the "·" separators, and the Uzbek file's
plain apostrophe (o'yin).

**Files.** `i18n/ru.po` or `i18n/uz.po`.

**Check.** `dotnet test --filter TranslationCoverageTests` passes; say in the pull request which part you read.

## 5. Start a new language

**Context.** A language is whatever .po file is in `i18n/`: the language menu lists it by the name in its header.

**Task.** Copy `i18n/deskarcade.pot` to `i18n/<code>.po` (es, de, pt, tr, kk…), fill in the `Language` and
`X-Language-Name` headers, and translate the menus and the At work text first. Untranslated lines stay in English,
so a partial language is fine to start with. See [TRANSLATING.md](TRANSLATING.md).

**Check.** The language shows in tray → Language and the menus read in it; `EveryPoFileNamesItsLanguage` passes.

## 6. More Typing Race texts

**Context.** Typing Race and Word Rain draw their passages and words from `TypingTexts`.

**Task.** Add ten passages to one language (English, Russian, Uzbek or code): public-domain or your own, two or three
sentences each, nothing that dates.

**Files.** `src/Games/TypingTexts.cs`.

**Check.** The typing tests pass, including the one that every text types on an ordinary keyboard.

## 7. New Bingo of Work squares

**Context.** Bingo of Work's card dabs desk events and small goals in the other games (`BingoCard.Desk`).

**Task.** Add six squares for games that have none yet: Pipeline, Load Balancer, FreeCell, Spider, Poker, Hearts.

**Files.** `src/Games/BingoRules.cs` (the squares and the counters they watch), `tests/DeskArcade.Tests/BingoTests.cs`,
and the square texts in the .po files.

**Check.** The Bingo tests pass; a dealt card can hold the new squares.

## 8. A third achievement for the games with two

**Context.** Can Knockdown, Brick Breaker, Bubble Pop, Tower Stack and Plinko have two achievements each; most games
have three.

**Task.** Give each a third, tied to a counter the game already reports (or a new `Host.Stats` counter).

**Files.** `src/Achievements.cs`, the game's file if it needs a new counter, and the titles and descriptions in the
.po files.

**Check.** `dotnet test` passes; the stats window lists the new achievements.

## 9. A daily challenge for more games

**Context.** The daily challenge (`Daily.Pool`) is the same for everyone that day and builds a streak.

**Task.** Add a second challenge for five games that have one (a different counter: swishes and baskets for Hoops
already works this way).

**Files.** `src/Daily.cs` and the challenge texts in the .po files.

**Check.** `dotnet test` passes, and the challenge counts in the running game (tray → the daily line).

## 10. A sound for a game that borrows one

**Context.** Every sound is synthesized in code (`src/Sound*.cs`); several games borrow generic clips.

**Task.** Give Curling a stone-on-ice rumble while a stone slides, and a knock when stones meet, as their own clips.

**Files.** A new `src/SoundCurling.cs` beside `SoundPool.cs`, a call in `Sound.Synthesize`, and the plays in
`src/Games/CurlingGame.cs`.

**Check.** Listen with `dotnet run -- --profile dev --demo --game curling`; nothing plays while nothing moves.

## 11. Tic-tac-toe on a bigger board

**Context.** Tic-tac-toe is three in a row on 3×3 (`LineRules`, shared with Connect Four).

**Task.** Add a 4×4 board where four in a row wins, switched by a chip under the board, with the computer at its four
levels.

**Files.** `src/Games/LineRules.cs`, `src/Games/LineGames.cs`, `tests/DeskArcade.Tests/` (a new test class).

**Check.** Tests for wins across, down and diagonally on 4×4, and each computer level beating the one below.

## 12. A keyboard shortcut for the ☰ menu

**Context.** Three global shortcuts exist: show or hide, next game, bring to cursor (`Shortcuts`, `HotkeyAction`).

**Task.** Add a fourth that opens the scoreboard's ☰ menu, with its key in the Shortcuts window.

**Files.** `src/Platform/IDesktopPlatform.cs` (`HotkeyAction`), `src/Shortcuts.cs`, `src/ShortcutsWindow.cs`,
`src/OverlayWindow.cs` (`OnHotkey`), and the platform layers' registration if they list the actions.

**Check.** The shortcut opens the menu on Windows and on an X11 desktop; the Shortcuts window saves it.
