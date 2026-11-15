// Fictional, deterministic review fixtures. User browsing stays a preview until Step 8. The live overview never uses these fixtures.
export type PreviewUser = {
  id: string;
  name: string;
  email: string;
  status: 'Confirmed' | 'Unconfirmed' | 'Locked';
  joined: string;
  administrator: boolean;
};
export const users: PreviewUser[] = [
  {
    id: 'alex',
    name: 'Alex Morgan',
    email: 'alex.morgan@example.com',
    status: 'Confirmed',
    joined: '2026-11-10',
    administrator: true,
  },
  {
    id: 'jamie',
    name: 'Jamie Chen',
    email: 'jamie.chen@example.com',
    status: 'Confirmed',
    joined: '2026-11-09',
    administrator: false,
  },
  {
    id: 'taylor',
    name: 'Taylor Reed',
    email: 'taylor.reed@example.com',
    status: 'Unconfirmed',
    joined: '2026-11-08',
    administrator: false,
  },
  {
    id: 'sam',
    name: 'Sam Rivera',
    email: 'sam.rivera@example.com',
    status: 'Confirmed',
    joined: '2026-11-07',
    administrator: false,
  },
  {
    id: 'jordan',
    name: 'Jordan Lee',
    email: 'jordan.lee@example.com',
    status: 'Locked',
    joined: '2026-11-06',
    administrator: false,
  },
  {
    id: 'casey',
    name: 'Casey Brooks',
    email: 'casey.brooks@example.com',
    status: 'Unconfirmed',
    joined: '2026-11-05',
    administrator: false,
  },
  {
    id: 'morgan',
    name: 'Morgan Park',
    email: 'morgan.park@example.com',
    status: 'Confirmed',
    joined: '2026-10-20',
    administrator: false,
  },
  {
    id: 'avery',
    name: 'Avery Quinn',
    email: 'avery.quinn@example.com',
    status: 'Confirmed',
    joined: '2026-10-15',
    administrator: false,
  },
];
export const applications = [
  { name: 'Campus Portal', initials: 'CP', description: 'Your everyday essentials' },
  { name: 'Course Manager', initials: 'CM', description: 'Teaching and learning' },
  { name: 'Team Workspace', initials: 'TW', description: 'Work, connected' },
];
export { initials } from '@/lib/person';
