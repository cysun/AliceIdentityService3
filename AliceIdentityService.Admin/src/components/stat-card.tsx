import { ArrowUpRight, type LucideIcon } from 'lucide-react';
import { Link } from 'react-router-dom';

type Props = {
  label: string;
  value?: number;
  icon: LucideIcon;
  to?: string;
  selected?: boolean;
};

export function StatCard({ label, value, icon: Icon, to, selected }: Props) {
  const content = (
    <>
      <div className="stat-card-heading">
        <Icon size={17} aria-hidden="true" />
        <span>{label}</span>
        {to && <ArrowUpRight className="stat-card-arrow" size={16} aria-hidden="true" />}
      </div>
      <strong>{value?.toLocaleString() ?? '…'}</strong>
    </>
  );
  return to ? (
    <Link className="stat-card" to={to} aria-current={selected || undefined}>
      {content}
    </Link>
  ) : (
    <div className="stat-card">{content}</div>
  );
}
