(function () {
  const header = document.querySelector(".site-header");
  const topBtn = document.querySelector(".back-to-top");
  const headerAt = 16;
  const topAt = 280;

  function sync() {
    const y = window.scrollY || document.documentElement.scrollTop;
    if (header) header.classList.toggle("is-scrolled", y > headerAt);
    if (topBtn) topBtn.classList.toggle("is-visible", y > topAt);
  }

  sync();
  window.addEventListener("scroll", sync, { passive: true });
  if (topBtn) {
    topBtn.addEventListener("click", function (ev) {
      ev.preventDefault();
      window.scrollTo({ top: 0, behavior: "smooth" });
    });
  }

  const searchForm = document.querySelector(".header-search");
  const searchInput = document.getElementById("busqueda");
  if (searchForm && searchInput) {
    const hasGrid = Boolean(document.getElementById("grid"));
    const initial = new URLSearchParams(location.search).get("q") || "";
    if (initial.trim()) {
      searchInput.value = initial;
      searchForm.classList.add("is-open");
    }

    function emitSearch(q) {
      if (!hasGrid) return;
      const url = new URL(location.href);
      if (q) url.searchParams.set("q", q);
      else url.searchParams.delete("q");
      history.replaceState(null, "", url.pathname + url.search + url.hash);
      document.dispatchEvent(new CustomEvent("pm-search", { detail: { q } }));
    }

    let timer = 0;
    searchForm.addEventListener("submit", function (ev) {
      const q = searchInput.value.trim();
      if (!searchForm.classList.contains("is-open")) {
        ev.preventDefault();
        searchForm.classList.add("is-open");
        searchInput.focus();
        return;
      }
      if (hasGrid) {
        ev.preventDefault();
        emitSearch(q);
      }
    });

    searchInput.addEventListener("input", function () {
      if (!hasGrid) return;
      clearTimeout(timer);
      timer = setTimeout(() => emitSearch(searchInput.value.trim()), 160);
    });

    searchInput.addEventListener("focus", function () {
      searchForm.classList.add("is-open");
    });

    document.addEventListener("keydown", function (ev) {
      if (ev.key !== "Escape" || !searchForm.classList.contains("is-open")) return;
      if (searchInput.value) {
        searchInput.value = "";
        emitSearch("");
      } else {
        searchForm.classList.remove("is-open");
        searchInput.blur();
      }
    });

    document.addEventListener("pointerdown", function (ev) {
      if (searchForm.contains(ev.target)) return;
      if (!searchInput.value.trim()) searchForm.classList.remove("is-open");
    });
  }
})();
