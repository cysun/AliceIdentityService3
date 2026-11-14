import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Checkbox } from '@/components/ui/checkbox';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
const schema = z.object({
  displayName: z.string().trim().min(2, 'Enter at least two characters.').max(80),
  email: z.email('Enter a valid email address.'),
});
type SampleForm = z.infer<typeof schema>;
export function ComponentsPage() {
  const [result, setResult] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors },
  } = useForm<SampleForm>({
    resolver: zodResolver(schema),
    defaultValues: { displayName: '', email: '' },
  });
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Component preview</h1>
          <p>Try the controls and keyboard interactions used throughout AIS3.</p>
        </div>
        <span className="sample-label">Local interactions only</span>
      </div>
      <div className="component-grid">
        <Card>
          <CardHeader>
            <CardTitle>Fields and validation</CardTitle>
            <CardDescription>A sample profile form. Nothing is sent or saved.</CardDescription>
          </CardHeader>
          <CardContent>
            <form
              className="sample-form"
              noValidate
              onSubmit={handleSubmit((values) =>
                setResult(`Preview validated for ${values.displayName}. No changes were saved.`),
              )}
            >
              <div>
                <Label htmlFor="sample-name">Display name</Label>
                <Input
                  id="sample-name"
                  autoComplete="off"
                  {...register('displayName')}
                  aria-invalid={!!errors.displayName}
                  aria-describedby={errors.displayName ? 'name-error' : undefined}
                />
                {errors.displayName && (
                  <p className="field-error" id="name-error">
                    {errors.displayName.message}
                  </p>
                )}
              </div>
              <div>
                <Label htmlFor="sample-email">Email address</Label>
                <Input
                  id="sample-email"
                  type="email"
                  autoComplete="off"
                  {...register('email')}
                  aria-invalid={!!errors.email}
                  aria-describedby={errors.email ? 'email-error' : undefined}
                />
                {errors.email && (
                  <p className="field-error" id="email-error">
                    {errors.email.message}
                  </p>
                )}
              </div>
              <div className="button-row">
                <Button type="submit">Validate sample</Button>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    reset();
                    setResult('');
                  }}
                >
                  Reset
                </Button>
              </div>
              <p role="status" className="form-result">
                {result}
              </p>
            </form>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Actions and feedback</CardTitle>
            <CardDescription>Base UI primitives with shared AIS styling.</CardDescription>
          </CardHeader>
          <CardContent className="component-stack">
            <div className="button-row">
              <Badge>Default</Badge>
              <Badge variant="outline">Outline</Badge>
              <Badge variant="secondary">Secondary</Badge>
            </div>
            <label className="checkbox-sample">
              <Checkbox /> Example checkbox
            </label>
            <Dialog>
              <DialogTrigger render={<Button variant="outline" />}>
                Open sample dialog
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Review sample action</DialogTitle>
                  <DialogDescription>
                    This dialog demonstrates focus handling and confirmation. It does not change an
                    account.
                  </DialogDescription>
                </DialogHeader>
                <DialogFooter>
                  <DialogClose render={<Button variant="outline" />}>Cancel</DialogClose>
                  <DialogClose render={<Button />} onClick={() => setConfirmed(true)}>
                    Confirm preview
                  </DialogClose>
                </DialogFooter>
              </DialogContent>
            </Dialog>
            <p role="status" className="form-result">
              {confirmed ? 'Sample action confirmed. No account was changed.' : ''}
            </p>
          </CardContent>
        </Card>
      </div>
    </>
  );
}
