// Browser-side helpers for WhyLine. Speech uses the browser's voices in the skeleton;
// the production path is Azure AI Speech (see README).
window.whyline = {
  voices: { en: "en-GB", ur: "ur-PK", es: "es-ES", ar: "ar-SA" },
  speak(text, lang) {
    if (!("speechSynthesis" in window) || !text) return false;
    const u = new SpeechSynthesisUtterance(text);
    u.lang = this.voices[lang] || "en-GB";
    const match = speechSynthesis.getVoices().find(v => v.lang && v.lang.toLowerCase().startsWith(u.lang.slice(0, 2)));
    if (match) u.voice = match;
    u.rate = 1.05;
    speechSynthesis.cancel();
    speechSynthesis.speak(u);
    return true;
  },
  stop() { if ("speechSynthesis" in window) speechSynthesis.cancel(); }
};
