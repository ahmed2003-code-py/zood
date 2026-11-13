// Pre-paint: apply language, direction and theme before the first render to avoid a flash of the
// wrong direction or theme. Mirrors src/lib/locale.ts and src/theme/preferences.ts.
// A file rather than an inline script, so the Content-Security-Policy can forbid inline scripts.
(function () {
  try {
    var params = new URLSearchParams(location.search);
    var read = function (key) {
      try {
        return localStorage.getItem(key);
      } catch (e) {
        return null;
      }
    };
    var lang = params.get('lang') || read('zood.locale');
    if (lang !== 'ar' && lang !== 'en') lang = 'ar';
    var pref = params.get('theme') || read('zood.theme');
    if (pref !== 'light' && pref !== 'dark' && pref !== 'system') pref = 'system';
    var dark = pref === 'dark' || (pref === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
    var root = document.documentElement;
    root.lang = lang;
    root.dir = lang === 'ar' ? 'rtl' : 'ltr';
    root.dataset.theme = dark ? 'dark' : 'light';
    root.style.colorScheme = dark ? 'dark' : 'light';
  } catch (e) {}
})();
