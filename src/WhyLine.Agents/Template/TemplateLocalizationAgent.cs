using WhyLine.Agents.Abstractions;
using WhyLine.Core.Domain;

namespace WhyLine.Agents.Template;

/// <summary>
/// Offline Localization: short, fact-driven lines written natively per language (not machine-translated English).
/// Used in Template mode and as the fallback when an AI localisation fails verification.
/// </summary>
public sealed class TemplateLocalizationAgent : ILocalizationAgent
{
    public Task<NarrativeDraft> LocalizeAsync(NarrativeDraft draft, NarrationRequest request, string? verifierFeedback, CancellationToken ct) =>
        Task.FromResult(Localize(draft, request.Context.Moment.Kind, request.AllFacts, request.Profile.Language));

    public static NarrativeDraft Localize(NarrativeDraft draft, MomentKind kind, IEnumerable<Fact> facts, string language)
    {
        if (language == "en") return draft;
        var f = new FactReader(facts);
        var line = Line(language, kind, f);
        return line is null
            ? draft
            : new NarrativeDraft(line.Value.Title, [line.Value.Sentence], $"template-localizer/{language}");
    }

    public static (string Title, string Sentence)? Line(string lang, MomentKind kind, FactReader f) => (lang, kind) switch
    {
        // ───────── Urdu ─────────
        ("ur", MomentKind.Goal) => ($"گول! {f["scorer"]}", $"{f["team"]} نے گول کر دیا، اسکور اب {f["score"]} ہے۔"),
        ("ur", MomentKind.BigChance) => ($"{f["player"]} کے پاس بڑا موقع", $"{f.Num("xg", "0.00")} xG کا موقع {f.Num("shot_distance")} میٹر سے ضائع ہو گیا۔"),
        ("ur", MomentKind.MomentumSwing) => ($"{f["team"]} نے کھیل پر قبضہ کر لیا",
            $"ان کا پریس فی منٹ {f.Num("pressures_before")} سے بڑھ کر {f.Num("pressures_now")} ہو گیا ہے، اور گیند پر قبضہ {f.Num("possession_before")}% سے {f.Num("possession_now")}% ہو گیا ہے۔"),
        ("ur", MomentKind.PressSurge) => ($"{f["team"]} کا دباؤ بڑھ گیا", $"وہ فی منٹ {f.Num("pressures_now")} بار پریس کر رہے ہیں، پہلے یہ {f.Num("pressures_before")} تھا۔"),
        ("ur", MomentKind.ControlSpell) => ($"{f["team"]} کھیل چلا رہے ہیں", $"پچھلے پانچ منٹ میں گیند {f.Num("possession")}% وقت ان کے پاس رہی اور {f.Num("completion")}% پاس مکمل ہوئے۔"),
        ("ur", MomentKind.DifficultPass) => ($"{f["player"]} کا شاندار پاس", $"{f.Num("distance")} میٹر کا پاس جس نے {f.Num("bypassed")} مخالف کھلاڑیوں کو پیچھے چھوڑ دیا۔ مشکل: {f.Num("difficulty")}/100۔"),
        ("ur", MomentKind.PassStreak) => ($"{f["player"]}: لگاتار {f.Num("streak")} پاس", $"{f["player"]} نے لگاتار {f.Num("streak")} پاس مکمل کیے ہیں۔"),
        ("ur", MomentKind.ShotSpeed) => ($"{f["player"]} کا زوردار شاٹ", $"یہ شاٹ {f.Num("shot_speed")} کلومیٹر فی گھنٹہ کی رفتار سے لگا۔"),
        ("ur", MomentKind.QuietPlayer) => ($"{f["player"]} خاموش ہیں", $"{f["player"]} نے {f.Num("minutes_without_touch")} منٹ سے گیند کو نہیں چھوا۔"),
        ("ur", MomentKind.TacticalShift) => ($"{f["team"]} کی حکمتِ عملی میں تبدیلی", $"{f["team"]} نے اپنے پریس کا انداز بدل دیا ہے۔"),
        ("ur", MomentKind.HalfTime) => ($"ہاف ٹائم: {f["home"]} {f["score"]} {f["away"]}", $"قبضہ {f.Num("home_possession")}% بمقابلہ {f.Num("away_possession")}%، شاٹس {f.Num("home_shots")} بمقابلہ {f.Num("away_shots")}۔"),
        ("ur", MomentKind.FullTime) => ($"فل ٹائم: {f["home"]} {f["score"]} {f["away"]}", $"قبضہ {f.Num("home_possession")}% بمقابلہ {f.Num("away_possession")}%، شاٹس {f.Num("home_shots")} بمقابلہ {f.Num("away_shots")}۔"),

        // ───────── Spanish ─────────
        ("es", MomentKind.Goal) => ($"¡GOL! {f["scorer"]}", $"{f["team"]} marca y el marcador es {f["score"]}."),
        ("es", MomentKind.BigChance) => ($"Ocasión clara para {f["player"]}", $"Una ocasión de {f.Num("xg", "0.00")} xG desde {f.Num("shot_distance")} metros que no terminó en gol."),
        ("es", MomentKind.MomentumSwing) => ($"{f["team"]} toma el control",
            $"Su presión subió de {f.Num("pressures_before")} a {f.Num("pressures_now")} por minuto y la posesión pasó del {f.Num("possession_before")}% al {f.Num("possession_now")}%."),
        ("es", MomentKind.PressSurge) => ($"{f["team"]} aprieta la presión", $"Presionan {f.Num("pressures_now")} veces por minuto; antes eran {f.Num("pressures_before")}."),
        ("es", MomentKind.ControlSpell) => ($"{f["team"]} manda en el partido", $"En los últimos cinco minutos ha tenido el {f.Num("possession")}% del balón y ha completado el {f.Num("completion")}% de sus pases."),
        ("es", MomentKind.DifficultPass) => ($"Pase de élite de {f["player"]}", $"Un pase de {f.Num("distance")} metros que superó a {f.Num("bypassed")} rivales. Dificultad: {f.Num("difficulty")}/100."),
        ("es", MomentKind.PassStreak) => ($"{f["player"]}: {f.Num("streak")} pases seguidos", $"{f["player"]} ha completado {f.Num("streak")} pases consecutivos."),
        ("es", MomentKind.ShotSpeed) => ($"Cañonazo de {f["player"]}", $"El disparo salió a {f.Num("shot_speed")} km/h."),
        ("es", MomentKind.QuietPlayer) => ($"{f["player"]} ha desaparecido", $"{f["player"]} lleva {f.Num("minutes_without_touch")} minutos sin tocar el balón."),
        ("es", MomentKind.TacticalShift) => ($"Cambio táctico de {f["team"]}", $"{f["team"]} cambia su forma de presionar."),
        ("es", MomentKind.HalfTime) => ($"Descanso: {f["home"]} {f["score"]} {f["away"]}", $"Posesión {f.Num("home_possession")}% a {f.Num("away_possession")}%, tiros {f.Num("home_shots")} a {f.Num("away_shots")}."),
        ("es", MomentKind.FullTime) => ($"Final: {f["home"]} {f["score"]} {f["away"]}", $"Posesión {f.Num("home_possession")}% a {f.Num("away_possession")}%, tiros {f.Num("home_shots")} a {f.Num("away_shots")}."),

        // ───────── Arabic ─────────
        ("ar", MomentKind.Goal) => ($"هدف! {f["scorer"]}", $"{f["team"]} يسجل والنتيجة الآن {f["score"]}."),
        ("ar", MomentKind.BigChance) => ($"فرصة كبيرة لـ{f["player"]}", $"فرصة بقيمة {f.Num("xg", "0.00")} xG من مسافة {f.Num("shot_distance")} متر لم تتحول إلى هدف."),
        ("ar", MomentKind.MomentumSwing) => ($"{f["team"]} يسيطر على المباراة",
            $"ارتفع ضغطهم من {f.Num("pressures_before")} إلى {f.Num("pressures_now")} في الدقيقة، وارتفع الاستحواذ من {f.Num("possession_before")}% إلى {f.Num("possession_now")}%."),
        ("ar", MomentKind.PressSurge) => ($"{f["team"]} يرفع وتيرة الضغط", $"يضغطون {f.Num("pressures_now")} مرة في الدقيقة بعد أن كانوا {f.Num("pressures_before")}."),
        ("ar", MomentKind.ControlSpell) => ($"{f["team"]} يتحكم في اللعب", $"استحوذوا على الكرة بنسبة {f.Num("possession")}% في آخر خمس دقائق وأكملوا {f.Num("completion")}% من تمريراتهم."),
        ("ar", MomentKind.DifficultPass) => ($"تمريرة مميزة من {f["player"]}", $"تمريرة بطول {f.Num("distance")} متر تجاوزت {f.Num("bypassed")} من لاعبي الخصم. الصعوبة: {f.Num("difficulty")}/100."),
        ("ar", MomentKind.PassStreak) => ($"{f["player"]}: {f.Num("streak")} تمريرة متتالية", $"أكمل {f["player"]} {f.Num("streak")} تمريرة متتالية."),
        ("ar", MomentKind.ShotSpeed) => ($"تسديدة صاروخية من {f["player"]}", $"انطلقت التسديدة بسرعة {f.Num("shot_speed")} كم/س."),
        ("ar", MomentKind.QuietPlayer) => ($"{f["player"]} غائب عن اللعب", $"لم يلمس {f["player"]} الكرة منذ {f.Num("minutes_without_touch")} دقيقة."),
        ("ar", MomentKind.TacticalShift) => ($"تغيير تكتيكي من {f["team"]}", $"{f["team"]} يغيّر أسلوب الضغط."),
        ("ar", MomentKind.HalfTime) => ($"نهاية الشوط الأول: {f["home"]} {f["score"]} {f["away"]}", $"الاستحواذ {f.Num("home_possession")}% مقابل {f.Num("away_possession")}%، والتسديدات {f.Num("home_shots")} مقابل {f.Num("away_shots")}."),
        ("ar", MomentKind.FullTime) => ($"نهاية المباراة: {f["home"]} {f["score"]} {f["away"]}", $"الاستحواذ {f.Num("home_possession")}% مقابل {f.Num("away_possession")}%، والتسديدات {f.Num("home_shots")} مقابل {f.Num("away_shots")}."),

        _ => null
    };

    /// <summary>Localised "catch me up" title.</summary>
    public static string CatchMeUpTitle(string lang, string score, string minute) => lang switch
    {
        "ur" => $"اب تک کا خلاصہ: {score}، منٹ {minute}",
        "es" => $"Ponte al día: {score}, minuto {minute}",
        "ar" => $"ملخص سريع: {score}، الدقيقة {minute}",
        _ => $"Catch me up: {score} after {minute} minutes"
    };
}
