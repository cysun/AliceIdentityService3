import { within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';

it('uses an icon-only appearance menu, remembers the selection, and follows System and other tabs', async () => {
  const system = new EventTarget();
  system.matches = false;
  vi.stubGlobal('matchMedia', () => system);
  localStorage.setItem('ais-theme', 'dark');
  const user = userEvent.setup();
  const picker = document.createElement('ais-theme-picker');
  try {
    await import('../../../AliceIdentityService/wwwroot/js/theme.js');
    document.body.append(picker);
    const ui = within(picker);
    const trigger = ui.getByRole('button', { name: 'Theme: dark' });
    const root = document.documentElement;
    expect(trigger.textContent.trim()).toBe('');
    expect(trigger.querySelector('use')).toHaveAttribute('href', '/brand/theme-icons.svg#sun-moon');
    expect(ui.queryByRole('menu')).toBeNull();
    expect(root.dataset.theme).toBe('dark');

    trigger.focus();
    await user.keyboard('{ArrowDown}');
    expect(ui.getByRole('menuitemradio', { name: 'Light' })).toHaveFocus();
    expect(ui.getByRole('menuitemradio', { name: 'Dark' })).toHaveAttribute('aria-checked', 'true');
    await user.keyboard('d');
    expect(ui.getByRole('menuitemradio', { name: 'Dark' })).toHaveFocus();
    await user.keyboard('{End}{Enter}');
    expect(trigger).toHaveFocus();
    expect(trigger).toHaveAccessibleName('Theme: system');
    expect(ui.queryByRole('menu')).toBeNull();
    expect(localStorage.getItem('ais-theme')).toBe('system');
    expect(root.dataset.theme).toBe('light');
    system.matches = true;
    system.dispatchEvent(new Event('change'));
    expect(root.dataset.theme).toBe('dark');

    window.dispatchEvent(new StorageEvent('storage', { key: 'ais-theme', newValue: 'light' }));
    expect(trigger).toHaveAccessibleName('Theme: light');
    expect(root.dataset.theme).toBe('light');
    await user.click(trigger);
    expect(ui.getByRole('menuitemradio', { name: 'Light' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    window.dispatchEvent(new StorageEvent('storage', { key: null }));
    expect(ui.getByRole('menuitemradio', { name: 'System' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    expect(root.dataset.theme).toBe('dark');
    await user.keyboard('{Escape}');
    expect(trigger).toHaveFocus();
    await user.click(trigger);
    await user.click(document.body);
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
  } finally {
    picker.remove();
    localStorage.clear();
    vi.unstubAllGlobals();
  }
});
