import Image from "next/image";
import { cn } from "@/lib/utils";

/** The WebReaper reaper, recoloured to the palette (violet cloak, white face +
 *  blade, ink outline) from the official logo.png shipped on every package. */
export function Logo({
  className,
  showWordmark = true,
}: {
  className?: string;
  showWordmark?: boolean;
}) {
  return (
    <span className={cn("inline-flex items-center gap-2.5", className)}>
      <Image
        src="/webreaper-mark.png"
        alt="WebReaper"
        width={32}
        height={32}
        className="h-8 w-8"
        priority
      />
      {showWordmark ? (
        <span className="text-[17px] font-extrabold tracking-tight text-foreground">
          WebReaper
        </span>
      ) : null}
    </span>
  );
}
