# Translating Desk Arcade

Desk Arcade speaks English, Uzbek and Russian. Every other language is welcome: Spanish, German, Portuguese, Turkish,
Kazakh or whatever you bring. This page covers how the text is stored, how to add or fix a language by hand, with
Poedit or on Hosted Weblate, and the few rules a translation must keep.

## Contents

- [How the text is stored](#how-the-text-is-stored)
- [Add a language](#add-a-language)
- [Fix or finish a language](#fix-or-finish-a-language)
- [The rules](#the-rules)
- [Check your translation](#check-your-translation)
- [Poedit](#poedit)
- [Hosted Weblate](#hosted-weblate)
- [For developers: new text](#for-developers-new-text)

## How the text is stored

The UI text lives in [gettext](https://www.gnu.org/software/gettext/) files in [`i18n/`](../i18n/), built into the
program:

| File | What it is |
|---|---|
| `i18n/deskarcade.pot` | The template: every English string the game shows, with nothing translated. A new language starts from it. The tests write it, so don't edit it by hand |
| `i18n/<code>.po` | One language: `uz.po` (Uzbek), `ru.po` (Russian). The English text is the `msgid`, the translation the `msgstr` |
| `i18n/parts/<name>.<code>.po` | New text for a language, waiting to be folded into its main file (see [For developers](#for-developers-new-text)) |

A language is simply whatever `.po` file is there: the build embeds every `i18n/*.po`, and the language menu (tray or ☰ →
Language) lists each one by the name in its `X-Language-Name` header. A new language needs no change to the code or the
project file. Anything a file doesn't translate shows in English.

With the language set to **Automatic** (the default), the game follows the system's display language when there is a
file for it: Windows' preferred display languages, and `LANGUAGE`, `LC_ALL`, `LC_MESSAGES` or `LANG` on Linux and macOS.

## Add a language

1. **Copy the template** to `i18n/<code>.po`, where `<code>` is the two-letter [ISO 639-1
   code](https://en.wikipedia.org/wiki/List_of_ISO_639_language_codes) in lower case: `es.po`, `de.po`, `pt.po`, `tr.po`,
   `kk.po`. Use the plain code, without a region (`pt`, not `pt_BR`): the game reads only the part before `_` or `-`, and
   the tests check that the file name and the `Language` header agree.
2. **Fill in the header** at the top: a comment line like the other files have, the `Language` code, and the language's
   name in the language itself in `X-Language-Name` (that is what the language menu shows):

   ```po
   # Desk Arcade: Spanish (es).
   msgid ""
   msgstr ""
   "Project-Id-Version: Desk Arcade\n"
   "Language: es\n"
   "X-Language-Name: Español\n"
   "MIME-Version: 1.0\n"
   "Content-Type: text/plain; charset=UTF-8\n"
   "Content-Transfer-Encoding: 8bit\n"
   ```

3. **Translate** each `msgstr`. Save the file as UTF-8. There are about 1,700 strings; you don't have to do them all at
   once, since an empty `msgstr` shows in English. The menus (the first strings in `uz.po` and `ru.po`), the scoreboard
   and the games you play most are a good start.
4. **Check it** (see [Check your translation](#check-your-translation)).
5. **Open a pull request** with the new file, titled for example "Add a Spanish translation". Say how far it goes, and
   whether you are a native speaker. A partial translation is welcome; so is a second pair of eyes on someone else's.

If you would rather not use git, open an issue and attach the file, and a maintainer will add it for you.

## Fix or finish a language

Edit `i18n/<code>.po` and open a pull request, or use [Hosted Weblate](#hosted-weblate) once it is set up. When the
English template has grown since a file was last updated, bring the new strings into the file first: in Poedit,
**Translation → Update from POT File…** and pick `i18n/deskarcade.pot`; with the gettext tools,
`msgmerge --update --no-wrap i18n/es.po i18n/deskarcade.pot`.

The Russian and Uzbek text was written without a native speaker's review for much of 1.8.x. Corrections from native
speakers are especially welcome there.

## The rules

These come from [`src/Strings.cs`](../src/Strings.cs), which reads the files, and from how the game uses the text.

- **Keep every placeholder.** `{0}`, `{1}` and so on are filled in when the text is shown: a score, a name, a time. A
  translation must have exactly the same placeholders as the English, in whatever order the sentence needs
  (`"{0} beat {1}"` can become `"{1} проиграл {0}"`). A placeholder missing, added or mistyped (`{O}`, `{ 0 }`) shows
  wrong text at best, and a number the game has no value for (`{2}` where the English has only `{0}` and `{1}`) makes
  the game fail when that text comes up. Entries with placeholders are marked `#, c-sharp-format`.
- **Keep the "·" separators** (a middle dot with a space either side) where the English has them. They separate the
  parts of a line, and the game joins more parts onto some lines with the same " · ".
- **Keep "×"** as it is, in multipliers such as "×2".
- **Keep the number last** in "Best {0}"-style strings. The compact scoreboard shows only the last word, so `"Best {0}"`
  must stay `"Рекорд {0}"`, never `"{0} — рекорд"`.
- **No plural forms.** Desk Arcade doesn't use gettext's plurals, so one translation has to read well for every number.
  Where your language changes the word with the number, word it so one form fits all, for example with a colon:
  `"{0} days in a row"` as `"Дней подряд: {0}"`.
- **One English string, one translation.** The same English text is translated once for every place it appears (there
  are no `msgctxt` contexts). If a word needs two different translations in two places, open an issue and the English
  can be split.
- **Keep it short.** Buttons, chips and the scoreboard have little room; a translation much longer than the English may
  be cut off. Check the places you are unsure of in the game.
- **Fuzzy means not yet.** An entry marked `#, fuzzy` (Poedit's "Needs work", Weblate's "Needs editing") is ignored and
  shows in English, the same as an empty one. Clear the mark once the translation is right.
- **Keep keyboard shortcuts and names as they are**: Ctrl+Alt+G, `arcade`, `--signal`, Claude Code, Durak, Tailscale.
- **Uzbek:** write the apostrophe in o', g' and the tutuq belgisi as the plain ASCII apostrophe (`'`), as `uz.po` does
  (o'yin, g'alaba), not ʻ, ʼ or ’.

## Check your translation

1. **Run the tests** (the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) is all you need):

   ```bash
   dotnet test tests/DeskArcade.Tests
   ```

   For every `.po` file they check that it names its language, that its file name matches the `Language` code, and that
   no English string is translated twice. For Uzbek and Russian they also check that every string is translated and
   that the placeholders match; once a new language is complete, it joins those checks so it stays complete.
2. **See it in the game.** Start a copy of your own, so the installed game and its settings are untouched, and pick your
   language in ☰ → Language:

   ```bash
   dotnet run -- --profile tr
   ```

   `--profile tr` keeps its settings, so later runs stay in your language. To look at one game without playing it, let
   it play itself and take a picture of the screen: `dotnet run -- --profile tr --demo --game durak --snapshot durak.png`.

## Poedit

[Poedit](https://poedit.net) is a free .po editor for Windows, macOS and Linux.

- **New language:** File → New from POT/PO File… and pick `i18n/deskarcade.pot`; choose the language, and save as
  `i18n/<code>.po`. Poedit writes the `Language` header; add the `X-Language-Name` line in a text editor afterwards, or
  ask for it in the pull request.
- **Existing language:** open `i18n/<code>.po`; Translation → Update from POT File… brings in new English strings.
- In Preferences → Advanced, keep **Preserve formatting of existing files** on (or line wrapping off), so a small change
  doesn't rewrap the whole file.
- Poedit's **Needs work** toggle is the fuzzy mark: the game ignores those entries until it is cleared.

## Hosted Weblate

[Hosted Weblate](https://hosted.weblate.org) lets volunteers translate in the browser, with suggestions, reviews and a
glossary, and sends the result back as pull requests. It is free for libre projects: its **Libre** plan is for public,
open-source projects and has the limits of the 160k plan (160,000 hosted strings; Desk Arcade has about 1,700 per
language).

### Setting it up (the maintainer, once)

1. **Sign in** at [hosted.weblate.org](https://hosted.weblate.org) with GitHub.
2. **Connect GitHub**: in your Weblate profile, connect the GitHub account and install the **Hosted Weblate** GitHub App
   on `BokhodirUrinboev/DeskArcade`. The app gives Weblate access to the repository, tells it about new commits, and
   lets it push translation branches and open pull requests; no webhook or deploy key needs setting up by hand.
3. **Create the project** at [hosted.weblate.org/hosting/](https://hosted.weblate.org/hosting/) and choose the
   **Libre** plan (gratis for public libre projects; Desk Arcade is MIT-licensed on public GitHub). Project name
   "Desk Arcade", website `https://github.com/BokhodirUrinboev/DeskArcade`.
4. **Add a component** from the repository with these settings:

   | Setting | Value |
   |---|---|
   | Component name | Desk Arcade (slug `deskarcade`) |
   | Version control system | GitHub pull request |
   | Source code repository | `https://github.com/BokhodirUrinboev/DeskArcade.git` |
   | Repository branch | `main` |
   | File format | Gettext PO file |
   | File mask | `i18n/*.po` |
   | Monolingual base language file | (empty: .po files carry their English) |
   | Template for new translations | `i18n/deskarcade.pot` |
   | Source language | English |
   | Adding new translation | Create new language file |
   | Language code style | Default based on the file format; add languages by their plain code (`pt`, not `pt_BR`) |
   | Translation licence | MIT License |

   Weblate finds `uz.po` and `ru.po` and imports them as they are.
5. **Add-ons** (component → Add-ons):
   - **Update PO files to match POT (msgmerge)**, so English strings added to `deskarcade.pot` reach every language.
   - **Customize gettext output**: turn long-line wrapping off, since the files keep each string on one line; with
     wrapping on, Weblate's first pull request would rewrap every file.
   - Optionally **Squash Git commits**, so each pull request is one commit.

### Before merging a pull request from Weblate

- **A new language** needs its `X-Language-Name` header (the name the language menu shows): Weblate writes `Language`
  but not that one. Add the line to the file on the pull request's branch; Weblate keeps it afterwards.
- **Parts first.** If `i18n/parts/` has files for a language, fold them into its main file before Weblate's msgmerge
  add-on runs: otherwise the same English string ends up both in a part and in the main file, and the tests fail
  (`NoTranslationKeyIsDefinedTwice`).
- The CI runs the tests on the pull request as on any other.

## For developers: new text

- Write UI text in English in the code, through `L.T("…")` or `L.F("… {0} …", value)` (see [`src/Loc.cs`](../src/Loc.cs)).
  The English is the key, so changing it untranslates it everywhere.
- Add the Uzbek and Russian translations to `i18n/uz.po` and `i18n/ru.po`, or, while several branches add text at
  once, to `i18n/parts/<feature>.uz.po` and `i18n/parts/<feature>.ru.po` (a header isn't needed there; they are folded
  into the main files later). `TranslationCoverageTests` fails until both languages have every string.
- Write the template again so new languages and Weblate see the new text:

  ```powershell
  $env:UPDATE_POT = 1; dotnet test tests/DeskArcade.Tests --filter TheTemplateListsEveryUiString; Remove-Item Env:UPDATE_POT
  ```

  ```bash
  UPDATE_POT=1 dotnet test tests/DeskArcade.Tests --filter TheTemplateListsEveryUiString
  ```
