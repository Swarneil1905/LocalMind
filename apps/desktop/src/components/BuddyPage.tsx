/**
 * BuddyPage — Full-screen companion interface.
 * Phase 2: Mascot + voice push-to-talk UI.
 * Voice is wired to come in Phase 2A — this sets up the visual shell.
 */
import { useEffect, useState } from "react";

type Mood = "idle" | "listening" | "thinking" | "happy" | "sleepy";

const MOOD_EMOJI: Record<Mood, string> = {
  idle: "🐶",
  listening: "👂",
  thinking: "🤔",
  happy: "😊",
  sleepy: "😴",
};

const MOOD_LABEL: Record<Mood, string> = {
  idle: "Ready",
  listening: "Listening…",
  thinking: "Thinking…",
  happy: "Got it!",
  sleepy: "Resting…",
};

const IDLE_MESSAGES = [
  "Hey! What's on your mind?",
  "I'm here — what can I help with?",
  "All ears. Speak freely.",
  "Fully local, fully private. Ask me anything.",
  "Nothing leaves this machine. You're safe here.",
];

export function BuddyPage() {
  const [mood, setMood] = useState<Mood>("idle");
  const [greeting, setGreeting] = useState(IDLE_MESSAGES[0]);
  const [spaceHeld, setSpaceHeld] = useState(false);
  const [voiceEnabled, setVoiceEnabled] = useState(false);
  const [lastResponse, setLastResponse] = useState<string | null>(null);

  // Rotate greeting every 8s when idle
  useEffect(() => {
    const msgs = IDLE_MESSAGES;
    let i = 0;
    const interval = setInterval(() => {
      i = (i + 1) % msgs.length;
      setGreeting(msgs[i]);
    }, 8000);
    return () => clearInterval(interval);
  }, []);

  // Space bar PTT (visual only for now — voice wires in Phase 2A)
  useEffect(() => {
    const onDown = (e: KeyboardEvent) => {
      if (e.code === "Space" && !e.repeat && !spaceHeld) {
        e.preventDefault();
        setSpaceHeld(true);
        setMood("listening");
      }
    };
    const onUp = (e: KeyboardEvent) => {
      if (e.code === "Space") {
        e.preventDefault();
        setSpaceHeld(false);
        setMood("thinking");
        // Simulate processing (voice not wired yet)
        setTimeout(() => {
          setMood("happy");
          setLastResponse("Voice input coming in the next update! For now, head to the Chat page to talk to me.");
          setTimeout(() => setMood("idle"), 3000);
        }, 1200);
      }
    };
    window.addEventListener("keydown", onDown);
    window.addEventListener("keyup", onUp);
    return () => {
      window.removeEventListener("keydown", onDown);
      window.removeEventListener("keyup", onUp);
    };
  }, [spaceHeld]);

  return (
    <div
      style={{
        flex: 1,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        gap: 0,
        position: "relative",
        overflow: "hidden",
        background: "radial-gradient(ellipse at 50% 60%, rgba(99,102,241,0.07) 0%, transparent 70%)",
      }}
    >
      {/* Background glow */}
      <div
        style={{
          position: "absolute",
          width: 400,
          height: 400,
          borderRadius: "50%",
          background: "radial-gradient(circle, rgba(99,102,241,0.06) 0%, transparent 70%)",
          top: "50%",
          left: "50%",
          transform: "translate(-50%, -55%)",
          pointerEvents: "none",
        }}
      />

      {/* Mascot */}
      <div
        style={{
          fontSize: 120,
          lineHeight: 1,
          marginBottom: 8,
          animation: mood === "idle" ? "buddyFloat 4s ease-in-out infinite" : undefined,
          transition: "transform 200ms ease",
          transform: spaceHeld ? "scale(1.08)" : "scale(1)",
          filter: mood === "sleepy" ? "saturate(0.6)" : undefined,
          userSelect: "none",
        }}
      >
        {MOOD_EMOJI[mood]}
      </div>

      {/* Mood label */}
      <div
        style={{
          fontSize: 11,
          fontWeight: 600,
          color: "var(--accent)",
          textTransform: "uppercase",
          letterSpacing: "0.1em",
          marginBottom: 12,
          opacity: 0.8,
        }}
      >
        {MOOD_LABEL[mood]}
      </div>

      {/* Speech bubble */}
      <div
        style={{
          maxWidth: 480,
          padding: "14px 22px",
          background: "var(--surface)",
          border: "1px solid var(--border)",
          borderRadius: 16,
          fontSize: 15,
          fontWeight: 500,
          color: "var(--text-2)",
          textAlign: "center",
          lineHeight: 1.55,
          marginBottom: 40,
          position: "relative",
          boxShadow: "0 4px 20px rgba(0,0,0,0.15)",
        }}
      >
        {lastResponse || greeting}
      </div>

      {/* PTT button */}
      <button
        onMouseDown={() => {
          setSpaceHeld(true);
          setMood("listening");
        }}
        onMouseUp={() => {
          setSpaceHeld(false);
          setMood("thinking");
          setTimeout(() => {
            setMood("happy");
            setLastResponse("Voice input coming in the next update! For now, head to the Chat page.");
            setTimeout(() => setMood("idle"), 3000);
          }, 1200);
        }}
        style={{
          display: "flex",
          alignItems: "center",
          gap: 10,
          padding: "14px 32px",
          background: spaceHeld ? "var(--accent)" : "var(--surface)",
          border: `2px solid ${spaceHeld ? "var(--accent)" : "var(--border)"}`,
          borderRadius: 50,
          fontSize: 14,
          fontWeight: 600,
          color: spaceHeld ? "white" : "var(--text-2)",
          cursor: "pointer",
          transition: "all 150ms ease",
          boxShadow: spaceHeld ? "0 0 20px rgba(99,102,241,0.4)" : "none",
          userSelect: "none",
        }}
      >
        <span style={{ fontSize: 18 }}>🎙</span>
        {spaceHeld ? "Listening… release to send" : "Hold Space (or click) to Talk"}
      </button>

      {/* Voice toggle */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 10,
          marginTop: 24,
          fontSize: 12,
          color: "var(--text-3)",
        }}
      >
        <button
          onClick={() => setVoiceEnabled((v) => !v)}
          style={{
            display: "flex",
            alignItems: "center",
            gap: 6,
            padding: "5px 12px",
            background: voiceEnabled ? "rgba(99,102,241,0.12)" : "var(--surface)",
            border: `1px solid ${voiceEnabled ? "rgba(99,102,241,0.4)" : "var(--border)"}`,
            borderRadius: 6,
            fontSize: 12,
            color: voiceEnabled ? "var(--accent)" : "var(--text-3)",
            cursor: "pointer",
          }}
        >
          <span>{voiceEnabled ? "🔊" : "🔇"}</span>
          Speak replies: {voiceEnabled ? "ON" : "OFF"}
        </button>

        <span style={{ color: "var(--text-3)" }}>·</span>

        <span style={{ fontSize: 11, color: "var(--text-3)" }}>
          Voice coming in Phase 2A
        </span>
      </div>

      {/* Animation keyframes */}
      <style>{`
        @keyframes buddyFloat {
          0%, 100% { transform: translateY(0px); }
          50%       { transform: translateY(-10px); }
        }
      `}</style>
    </div>
  );
}
