import { useId, type ReactNode } from 'react';
import { Link, type To } from 'react-router-dom';
import { ChevronRight, type LucideIcon } from 'lucide-react';
import { Badge } from '@/components/ui/badge';

type Props = {
  title: string;
  identifier: string | null;
  identifierLabel: string;
  icon: LucideIcon;
  badge?: string;
  children: ReactNode;
} & (
  | { to: To; actions?: never; description?: never }
  | { to?: never; actions?: ReactNode; description?: string | null }
);

export function DirectoryCard({
  to,
  title,
  identifier,
  identifierLabel,
  icon: Icon,
  badge,
  actions,
  description,
  children,
}: Props) {
  const summaryId = useId();
  const titleId = `${summaryId}-title`;
  const showIdentifier = identifier && identifier !== title;
  const content = (
    <>
      <div className="directory-card-heading">
        <div className="directory-card-title">
          <Icon className="directory-card-icon" size={40} aria-hidden="true" />
          <div>
            <h2 className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <span id={titleId}>{title}</span>{' '}
              {badge && <Badge variant="secondary">{badge}</Badge>}
            </h2>
            {showIdentifier && (
              <code id={`${summaryId}-identifier`}>
                <span className="sr-only">{identifierLabel}</span> {identifier}
              </code>
            )}
          </div>
        </div>
        {to !== undefined && (
          <ChevronRight className="directory-card-chevron" size={20} aria-hidden="true" />
        )}
        {actions && <div className="heading-actions">{actions}</div>}
      </div>
      {description && <p className="directory-card-description">{description}</p>}
      <dl id={summaryId} className="detail-list">
        {children}
      </dl>
    </>
  );
  return to !== undefined ? (
    <Link
      className="panel directory-card"
      to={to}
      aria-labelledby={titleId}
      aria-describedby={showIdentifier ? `${summaryId}-identifier ${summaryId}` : summaryId}
    >
      {content}
    </Link>
  ) : (
    <article className="panel directory-card" aria-labelledby={titleId}>
      {content}
    </article>
  );
}

export function IdentifierList({ values }: { values: string[] }) {
  if (!values.length) return 'None';
  return (
    <ul className="identifier-list">
      {values.map((value) => (
        <li key={value}>
          <code>{value}</code>
        </li>
      ))}
    </ul>
  );
}
