import { Link } from 'react-router-dom';
import { ArrowUpDown } from 'lucide-react';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { initials } from '@/lib/person';
import type { AdminUser, UserSort, UserStatus } from '@/lib/admin-api';

export function StatusBadge({ status }: { status: UserStatus }) {
  return (
    <Badge variant="outline" className={`status status-${status.toLowerCase()}`}>
      {status}
    </Badge>
  );
}

export function UserTable({
  data,
  sort,
  direction,
  onSort,
  listSearch,
}: {
  data: AdminUser[];
  sort: UserSort;
  direction: 'asc' | 'desc';
  onSort: (sort: UserSort) => void;
  listSearch: string;
}) {
  const columns: { key: UserSort; label: string }[] = [
    { key: 'name', label: 'User' },
    { key: 'status', label: 'Status' },
    { key: 'joined', label: 'Joined' },
  ];
  return (
    <Table aria-label="Users">
      <TableHeader>
        <TableRow>
          {columns.map(({ key, label }) => (
            <TableHead
              key={key}
              aria-sort={sort === key ? (direction === 'asc' ? 'ascending' : 'descending') : 'none'}
            >
              <button className="sort-button" onClick={() => onSort(key)}>
                {label}
                <ArrowUpDown size={13} aria-hidden="true" />
              </button>
            </TableHead>
          ))}
        </TableRow>
      </TableHeader>
      <TableBody>
        {data.map((user) => (
          <TableRow key={user.id}>
            <TableCell>
              <Link
                className="person"
                to={{ pathname: '/users/' + encodeURIComponent(user.id), search: listSearch }}
              >
                <span className="avatar" aria-hidden="true">
                  {initials(user.name)}
                </span>
                <span>
                  <strong>{user.name}</strong>
                  <small>{user.email || 'No email address'}</small>
                </span>
              </Link>
            </TableCell>
            <TableCell>
              <StatusBadge status={user.status} />
            </TableCell>
            <TableCell>
              <time dateTime={user.createdAt}>{new Date(user.createdAt).toLocaleDateString()}</time>
            </TableCell>
          </TableRow>
        ))}
        {data.length === 0 && (
          <TableRow>
            <TableCell colSpan={3} className="empty-state">
              No users match your filters.
            </TableCell>
          </TableRow>
        )}
      </TableBody>
    </Table>
  );
}
