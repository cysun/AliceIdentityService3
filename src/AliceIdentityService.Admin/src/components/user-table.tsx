import { Link } from 'react-router-dom';
import { ArrowUpDown } from 'lucide-react';
import {
  createColumnHelper,
  createCoreRowModel,
  createSortedRowModel,
  rowSortingFeature,
  sortFn_text,
  tableFeatures,
  useTable,
} from '@tanstack/react-table';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { Badge } from '@/components/ui/badge';
import { initials, type PreviewUser } from '@/preview-data';

const features = tableFeatures({
  rowSortingFeature,
  coreRowModel: createCoreRowModel(),
  sortedRowModel: createSortedRowModel(),
});
const column = createColumnHelper<typeof features, PreviewUser>();
export function StatusBadge({ status }: { status: PreviewUser['status'] }) {
  return (
    <Badge variant="outline" className={`status status-${status.toLowerCase()}`}>
      {status}
    </Badge>
  );
}
const columns = column.columns([
  column.accessor('name', {
    header: 'User',
    sortFn: sortFn_text,
    cell: ({ row }) => (
      <Link className="person" to={`/users/${row.original.id}`}>
        <span className="avatar">{initials(row.original.name)}</span>
        <span>
          <strong>{row.original.name}</strong>
          <small>{row.original.email}</small>
        </span>
      </Link>
    ),
  }),
  column.accessor('status', {
    header: 'Status',
    sortFn: sortFn_text,
    cell: ({ getValue }) => <StatusBadge status={getValue()} />,
  }),
  column.accessor('joined', { header: 'Joined', sortFn: sortFn_text }),
]);
export function UserTable({ data }: { data: PreviewUser[] }) {
  const table = useTable({ features, columns, data });
  return (
    <Table aria-label="Sample users">
      <TableHeader>
        {table.getHeaderGroups().map((group) => (
          <TableRow key={group.id}>
            {group.headers.map((header) => (
              <TableHead
                key={header.id}
                aria-sort={
                  header.column.getIsSorted() === 'asc'
                    ? 'ascending'
                    : header.column.getIsSorted() === 'desc'
                      ? 'descending'
                      : 'none'
                }
              >
                <button className="sort-button" onClick={header.column.getToggleSortingHandler()}>
                  <table.FlexRender header={header} />
                  <ArrowUpDown size={13} aria-hidden="true" />
                </button>
              </TableHead>
            ))}
          </TableRow>
        ))}
      </TableHeader>
      <TableBody>
        {table.getRowModel().rows.map((row) => (
          <TableRow key={row.id}>
            {row.getAllCells().map((cell) => (
              <TableCell key={cell.id}>
                <table.FlexRender cell={cell} />
              </TableCell>
            ))}
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
