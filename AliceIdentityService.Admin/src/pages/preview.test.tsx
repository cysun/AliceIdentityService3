import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { UsersPage, UserDetail } from './users';
import { ComponentsPage } from './components';

describe('review interactions', () => {
  it('searches names and emails and displays an empty state', async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter>
        <UsersPage />
      </MemoryRouter>,
    );
    const search = screen.getByRole('textbox', { name: 'Search users' });
    await user.type(search, ' JAMIE.CHEN@');
    expect(screen.getByRole('link', { name: /Jamie Chen/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Alex Morgan/ })).not.toBeInTheDocument();
    await user.clear(search);
    await user.type(search, 'nobody');
    expect(screen.getByText('No users match your filters.')).toBeInTheDocument();
  });
  it('honors a dashboard status link and sorts the filtered rows', async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/users?status=Unconfirmed']}>
        <UsersPage />
      </MemoryRouter>,
    );
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    await user.click(screen.getByRole('button', { name: 'User' }));
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Casey Brooks');
    await user.click(screen.getByRole('button', { name: 'User' }));
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('Taylor Reed');
  });
  it('opens a user detail route from a table row', async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/users']}>
        <Routes>
          <Route path="/users" element={<UsersPage />} />
          <Route path="/users/:id" element={<UserDetail />} />
        </Routes>
      </MemoryRouter>,
    );
    await user.click(screen.getByRole('link', { name: /Alex Morgan/ }));
    expect(screen.getByRole('heading', { name: 'Alex Morgan' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Claims' }));
    expect(screen.getByText('Sample claim: ais_admin = true')).toBeInTheDocument();
  });
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
