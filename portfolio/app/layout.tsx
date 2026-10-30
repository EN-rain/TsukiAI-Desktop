import type { Metadata } from "next";
import type { ReactNode } from "react";
import Script from "next/script";
import "../styles.css";

export const metadata: Metadata = {
  title: "Tsuki | Your night-sky companion",
  description: "Tsuki is a 24/7 AI voice companion living in your Discord voice channel.",
};

export default function RootLayout({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <html lang="en" data-theme="dark">
      <body>
        <Script src="https://www.googletagmanager.com/gtag/js?id=G-NRTXK8Y1GZ" strategy="afterInteractive" />
        <Script id="ga4-init" strategy="afterInteractive">
          {`window.dataLayer = window.dataLayer || [];
function gtag(){dataLayer.push(arguments);}
gtag('js', new Date());
gtag('config', 'G-NRTXK8Y1GZ');`}
        </Script>
        {children}
      </body>
    </html>
  );
}
