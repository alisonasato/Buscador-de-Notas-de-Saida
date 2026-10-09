/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ["./index.html", "../src/BuscadorNotas/wwwroot/index.html"],
  darkMode: "class",
  theme: {
    extend: {
      fontFamily: {
        sans: ["Inter", "ui-sans-serif", "system-ui", "Segoe UI", "Roboto", "sans-serif"],
        mono: ["ui-monospace", "SFMono-Regular", "Consolas", "Menlo", "monospace"],
      },
    },
  },
};
