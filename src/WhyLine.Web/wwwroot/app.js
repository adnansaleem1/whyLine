// Browser-side helpers for WhyLine. Cards are spoken with Azure AI Speech neural voices: the server synthesizes the
// audio and streams it here (whyline.play). If Speech is unavailable, the browser's own voices are used (whyline.speak).
window.whyline = {
  voices: { en: "en-GB", ur: "ur-PK", es: "es-MX", ar: "ar-SA" },
  audio: null,

  async play(streamRef, fallbackText, lang) {
    try {
      const buffer = await streamRef.arrayBuffer();
      const url = URL.createObjectURL(new Blob([buffer], { type: "audio/mpeg" }));
      this.stop();
      const audio = new Audio(url);
      audio.onended = () => URL.revokeObjectURL(url);
      this.audio = audio;
      await audio.play();
      return true;
    } catch (e) {
      console.warn("WhyLine: neural voice playback failed, using the browser voice", e);
      return this.speak(fallbackText, lang);
    }
  },

  speak(text, lang) {
    if (!("speechSynthesis" in window) || !text) return false;
    const u = new SpeechSynthesisUtterance(text);
    u.lang = this.voices[lang] || "en-GB";
    const match = speechSynthesis.getVoices().find(v => v.lang && v.lang.toLowerCase().startsWith(u.lang.slice(0, 2)));
    if (match) u.voice = match;
    u.rate = 1.05;
    this.stop();
    speechSynthesis.speak(u);
    return true;
  },

  stop() {
    if ("speechSynthesis" in window) speechSynthesis.cancel();
    if (this.audio) { this.audio.pause(); this.audio = null; }
  }
};
