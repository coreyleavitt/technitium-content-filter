/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./templates/**/*.html",
    "./static/js/**/*.js",
  ],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        // Override gray-50 to match Technitium's body background
        gray: {
          50: "#fafafa",
        },
        // Remap indigo to match Technitium's #6699ff primary
        indigo: {
          50:  "#eef2ff",
          100: "#dde6ff",
          200: "#b3c8ff",
          300: "#88aaff",
          400: "#7caaff",
          500: "#6699ff",
          600: "#6699ff",
          700: "#5580dd",
          800: "#4466bb",
          900: "#334d99",
        },
      },
    },
  },
  plugins: [],
};
