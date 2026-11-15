import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ComponentsPage } from './components';

describe('review interactions', () => {
  it('validates the local form without submitting an account request', async () => {
    const user = userEvent.setup();
    render(<ComponentsPage />);
    await user.click(screen.getByRole('button', { name: 'Validate sample' }));
    expect(await screen.findByText('Enter at least two characters.')).toBeInTheDocument();
    expect(screen.getByText('Enter a valid email address.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Display name'), 'Sample Person');
    await user.type(screen.getByLabelText('Email address'), 'sample@example.com');
    await user.click(screen.getByRole('button', { name: 'Validate sample' }));
    expect(
      await screen.findByText('Preview validated for Sample Person. No changes were saved.'),
    ).toBeInTheDocument();
  });
  it('closes the Base UI dialog with Escape and returns focus', async () => {
    const user = userEvent.setup();
    render(<ComponentsPage />);
    const trigger = screen.getByRole('button', { name: 'Open sample dialog' });
    await user.click(trigger);
    expect(screen.getByRole('dialog', { name: 'Review sample action' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });
});
