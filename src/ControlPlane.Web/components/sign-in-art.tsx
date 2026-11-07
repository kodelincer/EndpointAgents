/** Sign-in illustration: the user's PC, the assistant and the helpdesk as stations on one metro line. */
export function SignInArt() {
  const route = "M 540 256 L 470 256 C 400 256 350 238 280 238 C 210 238 160 256 90 256 L 20 256";
  return <figure className="auth-art" aria-hidden="true">
    <svg viewBox="0 0 560 300" xmlns="http://www.w3.org/2000/svg">
      <defs>
        <radialGradient id="art-glow" cx="50%" cy="48%" r="55%">
          <stop offset="0" stopColor="#4f8df5" stopOpacity=".32" />
          <stop offset="1" stopColor="#4f8df5" stopOpacity="0" />
        </radialGradient>
        <pattern id="art-dots" width="22" height="22" patternUnits="userSpaceOnUse">
          <circle cx="2" cy="2" r="1.2" fill="#8fb4f0" fillOpacity=".16" />
        </pattern>
        <linearGradient id="art-face" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#f3f7ff" />
          <stop offset="1" stopColor="#cfdcf5" />
        </linearGradient>
        <radialGradient id="art-fade" cx="50%" cy="50%" r="50%">
          <stop offset=".55" stopColor="#fff" />
          <stop offset="1" stopColor="#000" />
        </radialGradient>
        <mask id="art-mask"><rect width="560" height="300" fill="url(#art-fade)" /></mask>
        <path id="art-route" d={route} />
      </defs>

      <rect width="560" height="300" fill="url(#art-dots)" mask="url(#art-mask)" />
      <ellipse cx="280" cy="150" rx="260" ry="150" fill="url(#art-glow)" />

      {/* Metro line and stations */}
      <path d={route} fill="none" stroke="#3c5684" strokeWidth="9" strokeLinecap="round" />
      <path d={route} fill="none" stroke="#6aa6f8" strokeWidth="4" strokeLinecap="round" />
      <path className="art-flow" d={route} fill="none" stroke="#e9f2ff" strokeWidth="2"
        strokeLinecap="round" strokeDasharray="2 22" />
      {[[470, 256], [280, 238], [90, 256]].map(([x, y]) =>
        <circle key={x} cx={x} cy={y} r="9" fill="#1a2232" stroke="#6aa6f8" strokeWidth="4" />)}
      <circle className="art-packet" r="5" fill="#f4c56a">
        <animateMotion dur="4.5s" repeatCount="indefinite" keyPoints="0.1;0.9" keyTimes="0;1" calcMode="linear">
          <mpath href="#art-route" />
        </animateMotion>
      </circle>
      <circle className="art-packet" r="4" fill="#7fe0c8">
        <animateMotion dur="4.5s" begin="2.25s" repeatCount="indefinite" keyPoints="0.1;0.9" keyTimes="0;1" calcMode="linear">
          <mpath href="#art-route" />
        </animateMotion>
      </circle>

      {/* The user's PC, with the Tray chat on screen */}
      <g className="art-pc">
        <rect x="402" y="118" width="136" height="92" rx="12" fill="#253250" stroke="#3c5684" strokeWidth="2" />
        <rect x="411" y="127" width="118" height="70" rx="6" fill="#0f1724" />
        <rect x="459" y="135" width="62" height="15" rx="7.5" fill="#6aa6f8" />
        <rect x="419" y="156" width="74" height="15" rx="7.5" fill="#2f3f60" />
        <rect x="471" y="177" width="50" height="13" rx="6.5" fill="#6aa6f8" fillOpacity=".7" />
        <rect x="462" y="210" width="16" height="12" fill="#3c5684" />
        <rect x="440" y="221" width="60" height="7" rx="3.5" fill="#3c5684" />
      </g>
      <g className="art-bubble art-bubble-ask">
        <rect x="346" y="86" width="44" height="32" rx="14" fill="#f4c56a" />
        <path d="M 382 116 L 392 128 L 372 117 Z" fill="#f4c56a" />
        <text x="368" y="110" textAnchor="middle" fontSize="20" fontWeight="700" fill="#1a2232">؟</text>
      </g>

      {/* The assistant */}
      <g className="art-bot">
        <line x1="280" y1="110" x2="280" y2="94" stroke="#cfdcf5" strokeWidth="4" strokeLinecap="round" />
        <circle className="art-antenna" cx="280" cy="89" r="6" fill="#f4c56a" />
        <rect x="227" y="134" width="10" height="26" rx="5" fill="#9fb6e0" />
        <rect x="323" y="134" width="10" height="26" rx="5" fill="#9fb6e0" />
        <rect x="235" y="108" width="90" height="76" rx="24" fill="url(#art-face)" />
        <rect x="247" y="122" width="66" height="42" rx="15" fill="#1a2232" />
        <g className="art-eyes">
          <circle cx="266" cy="141" r="6.5" fill="#7fe0c8" />
          <circle cx="294" cy="141" r="6.5" fill="#7fe0c8" />
        </g>
        <path d="M 270 153 Q 280 160 290 153" fill="none" stroke="#7fe0c8" strokeWidth="3" strokeLinecap="round" />
        <rect x="252" y="190" width="56" height="34" rx="13" fill="#cfdcf5" />
        <circle cx="280" cy="207" r="5" fill="#7fe0c8" />
      </g>
      <g fill="#ffffff">
        <path className="art-spark" d="M 200 70 l 3 7 7 3 -7 3 -3 7 -3 -7 -7 -3 7 -3 Z" />
        <path className="art-spark s2" d="M 352 46 l 2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2 Z" />
        <path className="art-spark s3" d="M 486 72 l 2.5 6 6 2.5 -6 2.5 -2.5 6 -2.5 -6 -6 -2.5 6 -2.5 Z" />
        <path className="art-spark s2" d="M 60 64 l 2 5 5 2 -5 2 -2 5 -2 -5 -5 -2 5 -2 Z" />
      </g>

      {/* The helpdesk operator */}
      <g className="art-desk">
        <path d="M 48 232 Q 48 180 90 180 Q 132 180 132 232 Z" fill="#9fb6e0" />
        <circle cx="90" cy="146" r="25" fill="url(#art-face)" />
        <path d="M 62 146 A 28 28 0 0 1 118 146" fill="none" stroke="#6aa6f8" strokeWidth="5" strokeLinecap="round" />
        <rect x="57" y="139" width="10" height="18" rx="5" fill="#6aa6f8" />
        <rect x="113" y="139" width="10" height="18" rx="5" fill="#6aa6f8" />
        <path d="M 62 156 Q 64 172 82 171" fill="none" stroke="#6aa6f8" strokeWidth="3" strokeLinecap="round" />
        <circle cx="84" cy="171" r="3.5" fill="#6aa6f8" />
        <rect x="34" y="230" width="112" height="7" rx="3.5" fill="#3c5684" />
      </g>
      <g className="art-bubble art-bubble-done">
        <rect x="146" y="84" width="46" height="32" rx="14" fill="#7fe0c8" />
        <path d="M 154 114 L 146 126 L 166 115 Z" fill="#7fe0c8" />
        <path d="M 160 100 L 167 107 L 179 94" fill="none" stroke="#1a2232" strokeWidth="3.5"
          strokeLinecap="round" strokeLinejoin="round" />
      </g>
    </svg>
  </figure>;
}
