(() => {
  const key = 'ais-theme';
  const system = window.matchMedia('(prefers-color-scheme: dark)');
  const choices = [
    ['light', 'Light', 'sun'],
    ['dark', 'Dark', 'moon'],
    ['system', 'System', 'monitor'],
  ];
  let preference = 'system';
  let nextId = 0;

  function applyColorScheme() {
    document.documentElement.dataset.theme =
      preference === 'system' ? (system.matches ? 'dark' : 'light') : preference;
  }

  function selectTheme(value) {
    preference = value === 'light' || value === 'dark' ? value : 'system';
    applyColorScheme();
    for (const picker of document.querySelectorAll('ais-theme-picker')) picker.sync?.();
  }

  try {
    selectTheme(localStorage.getItem(key));
  } catch {
    applyColorScheme();
  }
  system.addEventListener('change', applyColorScheme);
  window.addEventListener('storage', (event) => {
    if (event.key === key || event.key === null) selectTheme(event.newValue);
  });

  function icon(name) {
    return `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <use href="/brand/theme-icons.svg#${name}"></use></svg>`;
  }

  class ThemePicker extends HTMLElement {
    connectedCallback() {
      if (!this.trigger) {
        const id = `ais-appearance-${++nextId}`;
        const items = choices.map(
          ([value, label, glyph]) => `
          <button type="button" class="header-menu-item" role="menuitemradio" tabindex="-1" data-theme-value="${value}">
            ${icon(glyph)}${label}<span class="theme-check">${icon('check')}</span>
          </button>`,
        );
        this.innerHTML = `
          <button type="button" class="theme-trigger" title="Change theme"
            aria-haspopup="menu" aria-controls="${id}" aria-expanded="false">${icon('sun-moon')}</button>
          <div id="${id}" class="header-menu-popup theme-popup" role="menu" aria-label="Appearance" hidden>
            <div class="header-menu-label" role="presentation">Appearance</div>
            <div class="header-menu-separator" role="separator"></div>
            ${items.join('')}
          </div>`;
        this.trigger = this.querySelector('.theme-trigger');
        this.menu = this.querySelector('[role="menu"]');
        this.items = Array.from(this.menu.querySelectorAll('[role="menuitemradio"]'));
        this.trigger.addEventListener('click', () =>
          this.menu.hidden ? this.open(0) : this.close(),
        );
        this.addEventListener('keydown', (event) => this.onKeyDown(event));
        this.menu.addEventListener('click', (event) => {
          const item = event.target.closest('[data-theme-value]');
          if (!item) return;
          selectTheme(item.dataset.themeValue);
          try {
            localStorage.setItem(key, preference);
          } catch {
            // The selection still applies to this page when storage is unavailable.
          }
          this.close(true);
        });
      }
      this.sync();
    }

    sync() {
      this.trigger.setAttribute('aria-label', `Theme: ${preference}`);
      for (const item of this.items) {
        item.setAttribute('aria-checked', String(item.dataset.themeValue === preference));
      }
    }

    onOutside = (event) => {
      if (!this.contains(event.target)) this.close();
    };

    open(index) {
      this.menu.hidden = false;
      this.trigger.setAttribute('aria-expanded', 'true');
      document.addEventListener('pointerdown', this.onOutside);
      document.addEventListener('focusin', this.onOutside);
      this.items[index].focus();
    }

    close(restoreFocus = false) {
      this.menu.hidden = true;
      this.trigger.setAttribute('aria-expanded', 'false');
      document.removeEventListener('pointerdown', this.onOutside);
      document.removeEventListener('focusin', this.onOutside);
      if (restoreFocus) this.trigger.focus();
    }

    disconnectedCallback() {
      this.close();
    }

    onKeyDown(event) {
      const last = this.items.length - 1;
      if (this.menu.hidden) {
        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
        this.open(event.key === 'ArrowDown' ? 0 : last);
      } else {
        const index = this.items.indexOf(document.activeElement);
        switch (event.key) {
          case 'ArrowDown':
            this.items[index === last ? 0 : index + 1].focus();
            break;
          case 'ArrowUp':
            this.items[index <= 0 ? last : index - 1].focus();
            break;
          case 'Home':
            this.items[0].focus();
            break;
          case 'End':
            this.items[last].focus();
            break;
          case 'Escape':
            this.close(true);
            break;
          case 'Tab':
            this.close(true);
            return;
          default: {
            if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey) return;
            const match = this.items.find((item) =>
              item.dataset.themeValue.startsWith(event.key.toLowerCase()),
            );
            if (!match) return;
            match.focus();
          }
        }
      }
      event.preventDefault();
    }
  }
  customElements.define('ais-theme-picker', ThemePicker);
})();
