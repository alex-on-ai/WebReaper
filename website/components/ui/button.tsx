import Link from "next/link";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils";

// The bench aesthetic: a hard ink border + offset block shadow that presses
// toward the pointer on hover. Primary carries the violet fill.
const variants = {
  primary:
    "border-2 border-border-strong bg-accent text-accent-foreground shadow-[3px_3px_0_var(--shadow-ink)] hover:bg-accent-strong hover:-translate-x-0.5 hover:-translate-y-0.5 hover:shadow-[5px_5px_0_var(--shadow-ink)] active:translate-x-0 active:translate-y-0 active:shadow-[2px_2px_0_var(--shadow-ink)]",
  secondary:
    "border-2 border-border-strong bg-surface text-foreground shadow-[3px_3px_0_var(--shadow-ink)] hover:-translate-x-0.5 hover:-translate-y-0.5 hover:shadow-[5px_5px_0_var(--shadow-ink)] active:translate-x-0 active:translate-y-0 active:shadow-[2px_2px_0_var(--shadow-ink)]",
  outline:
    "border-2 border-border-strong text-foreground hover:bg-surface-2",
  ghost: "text-muted hover:text-foreground hover:bg-surface-2",
} as const;

const sizes = {
  sm: "h-9 px-3.5 text-sm",
  md: "h-11 px-5 text-sm",
  lg: "h-12 px-6 text-[15px]",
} as const;

const base =
  "inline-flex items-center justify-center gap-2 rounded-lg font-semibold transition-all duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/70 disabled:pointer-events-none disabled:opacity-50";

type CommonProps = {
  variant?: keyof typeof variants;
  size?: keyof typeof sizes;
  className?: string;
  children?: ReactNode;
};

type ButtonAsButton = CommonProps &
  ButtonHTMLAttributes<HTMLButtonElement> & { href?: undefined };
type ButtonAsLink = CommonProps & {
  href: string;
  target?: string;
  rel?: string;
};

export function Button(props: ButtonAsButton | ButtonAsLink) {
  const { variant = "primary", size = "md", className, children } = props;
  const classes = cn(base, variants[variant], sizes[size], className);

  if ("href" in props && props.href) {
    const isExternal = /^(https?:|mailto:)/.test(props.href);
    if (isExternal) {
      return (
        <a
          href={props.href}
          target={props.target ?? "_blank"}
          rel={props.rel ?? "noreferrer noopener"}
          className={classes}
        >
          {children}
        </a>
      );
    }
    return (
      <Link href={props.href} className={classes}>
        {children}
      </Link>
    );
  }

  const {
    variant: _variant,
    size: _size,
    className: _className,
    children: _children,
    ...rest
  } = props as ButtonAsButton;
  void _variant;
  void _size;
  void _className;
  void _children;
  return (
    <button className={classes} {...rest}>
      {children}
    </button>
  );
}
