import {
  Button as FluentButton,
  Dialog,
  DialogSurface,
  DialogBody,
  DialogTitle,
  DialogContent,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import {
  ArrowRightRegular as ArrowRight,
  DocumentRegular as FileText,
  ArrowSyncRegular as LoaderCircle,
  DismissRegular as X,
} from '@fluentui/react-icons';
import type { ButtonHTMLAttributes, ReactNode } from 'react';

export function Button({
  className = '',
  variant = 'primary',
  busy = false,
  children,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  busy?: boolean;
}) {
  return (
    <FluentButton
      appearance={variant === 'primary' ? 'primary' : variant === 'ghost' ? 'subtle' : 'secondary'}
      className={`study-button ${variant === 'danger' ? 'study-danger' : ''} ${className}`}
      {...props}
      disabled={props.disabled || busy}
    >
      {busy && <LoaderCircle fontSize={15} className="spin" />}
      {children}
    </FluentButton>
  );
}

export function Notice({
  children,
  kind = 'info',
}: {
  children: ReactNode;
  kind?: 'info' | 'warning' | 'error' | 'success';
}) {
  return (
    <MessageBar intent={kind} className="study-notice">
      <MessageBarBody>{children}</MessageBarBody>
    </MessageBar>
  );
}

export function EmptyState({
  title,
  children,
  action,
}: {
  title: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="empty-state">
      <span className="empty-icon">
        <FileText fontSize={28} />
      </span>
      <h3>{title}</h3>
      <p>{children}</p>
      {action}
    </div>
  );
}

export function Modal({
  open,
  onOpenChange,
  title,
  description,
  children,
  drawer = false,
  closeLabel = '关闭',
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  children: ReactNode;
  drawer?: boolean;
  closeLabel?: string;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, detail) => onOpenChange(detail.open)}>
      <DialogSurface className={drawer ? 'study-dialog study-drawer' : 'study-dialog'}>
        <DialogBody>
          <DialogTitle
            action={
              <FluentButton
                appearance="subtle"
                aria-label={closeLabel}
                onClick={() => onOpenChange(false)}
                icon={<X fontSize={18} />}
              />
            }
          >
            {title}
          </DialogTitle>
          <DialogContent>
            <p className="dialog-description">{description}</p>
            {children}
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function SectionTitle({
  eyebrow,
  title,
  children,
}: {
  eyebrow?: string;
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className="section-title">
      <div>
        {eyebrow && <span className="eyebrow">{eyebrow}</span>}
        <h2>{title}</h2>
      </div>
      {children}
    </div>
  );
}

export function StageLink({ children }: { children: ReactNode }) {
  return (
    <span className="stage-link">
      {children}
      <ArrowRight fontSize={14} />
    </span>
  );
}
