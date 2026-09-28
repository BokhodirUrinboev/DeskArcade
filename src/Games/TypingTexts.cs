using System;
using System.Collections.Generic;

namespace DeskArcade.Games;

/// <summary>What the typing games give you to type: prose in one of the three languages, or code.</summary>
public enum TypingKind { English, Russian, Uzbek, Code }

/// <summary>
/// The texts for Typing Race and the words for Word Rain. Everything types on an ordinary keyboard: straight quotes and
/// apostrophes (Uzbek o' and g' too), a plain hyphen for a dash. Code is several lines; after Enter the indentation of
/// the next line is skipped, as an editor would put it there.
/// </summary>
public static class TypingTexts
{
    public static readonly TypingKind[] Kinds = { TypingKind.English, TypingKind.Russian, TypingKind.Uzbek, TypingKind.Code };

    /// <summary>The setting value of each kind ("auto" follows the interface language).</summary>
    public static string SettingOf(TypingKind kind) => kind switch
    {
        TypingKind.Russian => "ru",
        TypingKind.Uzbek => "uz",
        TypingKind.Code => "code",
        _ => "en",
    };

    /// <summary>The kind a setting stands for; "auto" (or anything unknown) takes the interface language.</summary>
    public static TypingKind FromSetting(string? setting, string uiLanguage) => setting switch
    {
        "en" => TypingKind.English,
        "ru" => TypingKind.Russian,
        "uz" => TypingKind.Uzbek,
        "code" => TypingKind.Code,
        _ => uiLanguage switch { "ru" => TypingKind.Russian, "uz" => TypingKind.Uzbek, _ => TypingKind.English },
    };

    /// <summary>The next kind in the chip's cycle.</summary>
    public static TypingKind Next(TypingKind kind) => Kinds[(Array.IndexOf(Kinds, kind) + 1) % Kinds.Length];

    /// <summary>The chip's label, shown untranslated like the language menu (each is written in its own language).</summary>
    public static string Label(TypingKind kind) => kind switch
    {
        TypingKind.Russian => "Русский",
        TypingKind.Uzbek => "O'zbekcha",
        TypingKind.Code => "</> Code",
        _ => "English",
    };

    public static IReadOnlyList<string> Passages(TypingKind kind) => kind switch
    {
        TypingKind.Russian => Russian,
        TypingKind.Uzbek => Uzbek,
        TypingKind.Code => Code,
        _ => English,
    };

    public static IReadOnlyList<string> Words(TypingKind kind) => kind switch
    {
        TypingKind.Russian => RussianWords,
        TypingKind.Uzbek => UzbekWords,
        TypingKind.Code => CodeWords,
        _ => EnglishWords,
    };

    static readonly string[] English =
    {
        "The build is green, the coffee is warm, and the bug that hid all morning turned out to be a missing comma.",
        "Every great program starts as a small one that somebody refused to give up on.",
        "Write code as if the next person to read it knows where you sit and has a very long lunch break.",
        "A good test fails for exactly one reason, and it tells you that reason in plain words.",
        "The meeting could have been an email, the email could have been a message, and the message could have been a thumbs up.",
        "Rename the variable, delete the dead code, and leave the file a little cleaner than you found it.",
        "Small steps, run the tests, commit often. Big steps, hope for the best, and read the logs until sunset.",
        "The cat walked across the keyboard and somehow fixed the flaky test. Nobody asked how.",
        "Typing fast is nice, but typing the right thing the first time is even faster.",
        "Some days you ship a feature before lunch. Other days you spend the whole afternoon renaming one folder.",
        "Before you optimize anything, measure it twice and make sure it is actually slow.",
        "The best error message says what went wrong, why it went wrong, and what to try next.",
        "Sphinx of black quartz, judge my vow: this time the release notes get written before the release.",
        "Documentation is a letter to your future self, who will not remember any of this.",
        "Keep your functions short, your names honest, and your coffee within reach but far from the laptop.",
        "Nothing lasts longer than a quick temporary fix that happens to work on the first try.",
        "The deploy went out at five on a Friday, and everyone politely pretended not to notice.",
        "A clear commit message today saves an hour of detective work next spring.",
        "When the terminal is busy, the arcade is open. When the terminal is done, it is back to work.",
        "The quick brown fox jumps over the lazy dog, then files a bug report about the dog.",
    };

    static readonly string[] Russian =
    {
        "Тише едешь - дальше будешь. Но дедлайн, к сожалению, об этом ничего не знает.",
        "Семь раз отмерь, один раз отрежь, а потом еще раз запусти тесты.",
        "Без труда не выловишь и рыбку из пруда, а без логов не найдешь и ошибку в коде.",
        "Век живи - век учись. Особенно если каждый год выходит новый фреймворк.",
        "Делу время, потехе час. Пока идет сборка, можно немного и поиграть.",
        "Глаза боятся, а руки делают. Большая задача решается маленькими шагами.",
        "Утро вечера мудренее: сложную ошибку лучше искать на свежую голову.",
        "Хороший код читается как книга, а плохой - как инструкция к шкафу без картинок.",
        "Кот прошел по клавиатуре и случайно починил сборку. Никто так и не понял, как.",
        "Не откладывай на завтра то, что можно закоммитить сегодня.",
        "Лучше один раз написать тест, чем сто раз проверять все руками.",
        "Кто рано встает, тот первым видит, что ночная сборка упала.",
        "Повторение - мать учения, а автоматизация - его лучшая подруга.",
        "Быстро печатать приятно, но печатать без ошибок еще приятнее.",
        "Слово не воробей: вылетит - не поймаешь. Поэтому перечитай письмо перед отправкой.",
        "Чем проще решение, тем легче его поддерживать через год.",
    };

    static readonly string[] Uzbek =
    {
        "Bilagi zo'r birni yiqar, bilimi zo'r mingni yiqar.",
        "Ko'p o'qigan ko'p biladi. Ko'p yozgan esa tez yozadi.",
        "Sabr tagi - sariq oltin. Dastur yig'ilishini ham sabr bilan kuting.",
        "Til - dilning kaliti, kod esa dasturchining tili.",
        "Olim bo'lsang, olam seniki. Har kuni ozgina yangi narsa o'rgan.",
        "Birlashgan o'zar, birlashmagan to'zar. Jamoa bo'lib ishlash ishni yengillashtiradi.",
        "Yaxshi so'z - jon ozig'i. Hamkasbingizga bugun yaxshi so'z ayting.",
        "Mehnatning tagi - rohat. Ish tugagach, bir o'yin o'ynasa ham bo'ladi.",
        "Yetti o'lchab, bir kes. Kodni yuborishdan oldin testlarni tekshir.",
        "Mushuk klaviatura ustidan yurib o'tdi va negadir xatoni tuzatib qo'ydi.",
        "Tez yozish yaxshi, lekin xatosiz yozish undan ham yaxshi.",
        "Bugungi ishni ertaga qoldirma. Bugun yozilgan test ertaga seni qutqaradi.",
        "Kitob - bilim manbai, hujjat esa kelajakdagi o'zingga yozilgan xat.",
        "Oz-oz o'rganib dono bo'lur, qatra-qatra yig'ilib daryo bo'lur.",
        "Kichik qadamlar bilan yurgan uzoqqa boradi. Har bir o'zgarishdan keyin testlarni ishga tushir.",
        "Aql yoshda emas, boshda. Yaxshi g'oya har kimdan chiqishi mumkin.",
    };

    static readonly string[] Code =
    {
        "foreach (var game in games)\n{\n    if (game.Id == id) return game;\n}\nreturn null;",
        "def fizzbuzz(n):\n    for i in range(1, n + 1):\n        print(\"Fizz\" * (i % 3 == 0) + \"Buzz\" * (i % 5 == 0) or i)",
        "const total = items\n  .filter(item => item.inStock)\n  .reduce((sum, item) => sum + item.price, 0);",
        "SELECT name, COUNT(*) AS wins\nFROM races\nWHERE wpm > 60\nGROUP BY name\nORDER BY wins DESC;",
        "git switch -c feature/typing\ndotnet test && git commit -am \"Add a typing race\"\ngit push -u origin HEAD",
        "func add(a, b int) int {\n    return a + b\n}",
        "fn main() {\n    let words = vec![\"fast\", \"faster\", \"fastest\"];\n    for w in &words {\n        println!(\"{w}\");\n    }\n}",
        "interface Player {\n  name: string;\n  wpm: number;\n  isCpu?: boolean;\n}",
        "var best = scores.Where(s => s.Game == \"typing\")\n                 .OrderByDescending(s => s.Wpm)\n                 .First();",
        "<button class=\"start\" onclick=\"race()\">\n  Start the race\n</button>",
        "class Timer:\n    def __enter__(self):\n        self.start = time.time()\n        return self",
        "{\n  \"game\": \"typing\",\n  \"wpm\": 72,\n  \"accuracy\": 0.98\n}",
        "while (!done)\n{\n    done = queue.TryDequeue(out var job) && job.Run();\n}",
        "for f in *.log; do\n    grep -c \"ERROR\" \"$f\"\ndone",
    };

    static readonly string[] EnglishWords =
    {
        "code", "build", "test", "merge", "branch", "commit", "deploy", "coffee", "desk", "screen", "mouse", "window",
        "cursor", "pixel", "folder", "file", "save", "undo", "redo", "paste", "copy", "print", "debug", "error", "fix",
        "patch", "ship", "task", "note", "email", "chat", "call", "team", "lunch", "break", "office", "chair", "lamp",
        "plant", "paper", "pencil", "stapler", "printer", "monitor", "laptop", "server", "cloud", "cable", "router",
        "script", "method", "class", "object", "value", "string", "number", "array", "list", "loop", "vector", "stack",
        "queue", "cache", "token", "input", "output", "signal", "timer", "clock", "alarm", "update", "install", "release",
        "version", "review", "request", "report", "ticket", "sprint", "agile", "planning", "sketch", "draft", "idea",
        "focus", "quick", "brown", "fox", "lazy", "dog", "jump", "happy", "sunny", "rain", "storm", "thunder", "cloudy",
        "apple", "banana", "cookie", "donut", "pizza", "noodle", "tea", "water", "juice", "snack", "music", "podcast",
        "rocket", "planet", "comet", "galaxy", "orbit", "laser", "robot", "engine", "gadget", "widget", "button", "switch",
        "arcade", "player", "score", "level", "bonus", "combo", "streak", "record", "winner", "puzzle", "maze", "quest",
    };

    static readonly string[] RussianWords =
    {
        "код", "сборка", "тест", "ветка", "коммит", "релиз", "кофе", "стол", "экран", "мышь", "окно", "курсор", "папка",
        "файл", "ошибка", "задача", "письмо", "звонок", "команда", "обед", "перерыв", "офис", "стул", "лампа", "бумага",
        "карандаш", "принтер", "монитор", "ноутбук", "сервер", "облако", "кабель", "скрипт", "метод", "класс", "объект",
        "значение", "строка", "число", "массив", "список", "цикл", "стек", "очередь", "кэш", "ввод", "вывод", "сигнал",
        "таймер", "часы", "будильник", "обновление", "версия", "отчет", "заявка", "спринт", "план", "идея", "фокус",
        "быстро", "лиса", "собака", "прыжок", "солнце", "дождь", "гроза", "облачно", "яблоко", "банан", "печенье",
        "пицца", "чай", "вода", "сок", "музыка", "ракета", "планета", "комета", "галактика", "орбита", "лазер", "робот",
        "двигатель", "кнопка", "аркада", "игрок", "счет", "уровень", "бонус", "рекорд", "победа", "загадка", "лабиринт",
        "клавиша", "буква", "слово", "фраза", "текст", "скорость", "точность", "минута", "секунда", "неделя", "пятница",
        "понедельник", "утро", "вечер", "ночь", "друг", "коллега", "начальник", "проект", "решение", "вопрос", "ответ",
    };

    static readonly string[] UzbekWords =
    {
        "kod", "test", "fayl", "papka", "xato", "vazifa", "xat", "qo'ng'iroq", "jamoa", "tushlik", "tanaffus", "ofis",
        "stul", "chiroq", "qog'oz", "qalam", "printer", "monitor", "noutbuk", "server", "bulut", "kabel", "dastur",
        "sinf", "qiymat", "satr", "son", "massiv", "ro'yxat", "sikl", "navbat", "kirish", "chiqish", "signal", "taymer",
        "soat", "yangilash", "versiya", "hisobot", "reja", "g'oya", "diqqat", "tez", "tulki", "it", "sakrash", "quyosh",
        "yomg'ir", "momaqaldiroq", "bulutli", "olma", "banan", "pechenye", "choy", "suv", "sharbat", "musiqa", "raketa",
        "sayyora", "kometa", "galaktika", "orbita", "robot", "dvigatel", "tugma", "o'yinchi", "hisob", "daraja", "bonus",
        "rekord", "g'alaba", "jumboq", "labirint", "klaviatura", "harf", "so'z", "ibora", "matn", "tezlik", "aniqlik",
        "daqiqa", "soniya", "hafta", "juma", "dushanba", "ertalab", "kechqurun", "tun", "do'st", "hamkasb", "rahbar",
        "loyiha", "yechim", "savol", "javob", "kitob", "bilim", "maktab", "ustoz", "shogird", "bahor", "yoz", "kuz",
        "qish", "tog'", "daryo", "dengiz", "shahar", "ko'cha", "uy", "eshik", "deraza", "stol", "kompyuter", "sichqoncha",
    };

    static readonly string[] CodeWords =
    {
        "var", "let", "const", "if", "else", "for", "while", "return", "class", "struct", "enum", "void", "int", "bool",
        "string", "null", "true", "false", "new", "this", "self", "async", "await", "yield", "import", "export", "from",
        "public", "private", "static", "sealed", "override", "virtual", "interface", "namespace", "using", "try", "catch",
        "finally", "throw", "switch", "case", "break", "continue", "def", "lambda", "print", "range", "len", "map",
        "filter", "reduce", "=>", "!=", "==", "&&", "||", "++", "+=", "::", "->", "[]", "{}", "()", "i++", "x => x",
        "git", "push", "pull", "merge", "rebase", "commit", "grep", "sudo", "chmod", "curl", "npm", "dotnet", "cargo",
        "SELECT", "FROM", "WHERE", "JOIN", "GROUP BY", "ORDER BY", "LIMIT", "INSERT", "UPDATE", "DELETE", "main()",
        "args", "argv", "stdin", "stdout", "stderr", "TODO", "FIXME", "0x1F", "#include", "@Override", "nil", "None",
    };
}
