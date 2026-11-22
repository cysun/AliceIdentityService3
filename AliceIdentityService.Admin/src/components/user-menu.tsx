import { Menu } from '@base-ui/react/menu';
import { ShieldCheck } from 'lucide-react';
import { SignOutForm } from '@/components/sign-out-form';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';

export function UserMenu({
  name,
  initials,
  email,
}: {
  name: string;
  initials: string;
  email: string | null;
}) {
  return (
    <Menu.Root modal={false}>
      <Menu.Trigger
        render={
          <Button
            variant="ghost"
            size="icon"
            className="user-menu-trigger"
            aria-label={`User menu for ${name}`}
          />
        }
      >
        <span aria-hidden="true">{initials}</span>
      </Menu.Trigger>
      <Menu.Portal keepMounted>
        <Menu.Positioner className="user-menu-positioner" align="end" sideOffset={4}>
          <Menu.Popup className="header-menu-popup user-menu-popup" aria-label="User menu">
            <div className="user-menu-identity">
              <strong>{name}</strong>
              {email && <small>{email}</small>}
              <Badge variant="secondary">
                <ShieldCheck aria-hidden="true" />
                Administrator
              </Badge>
            </div>
            <Menu.Separator className="header-menu-separator" />
            <SignOutForm inMenu />
          </Menu.Popup>
        </Menu.Positioner>
      </Menu.Portal>
    </Menu.Root>
  );
}
