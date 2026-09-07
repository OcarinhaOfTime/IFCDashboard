Future Decisions Unity WebGL Template
======================================

Installation
------------

Copy the contents of this folder into:

Assets/WebGLTemplates/FutureDecisions/

The expected project structure is:

Assets/
└── WebGLTemplates/
    └── FutureDecisions/
        ├── index.html
        ├── thumbnail.png
        └── TemplateData/
            └── future-decisions-logo.png

Then select the template in:

Project Settings
→ Player
→ Web
→ Resolution and Presentation
→ Web Template
→ FutureDecisions

Notes
-----

- The page fills the browser viewport.
- The Unity canvas automatically resizes with the page.
- The Future Decisions header remains visible over the scene.
- The Fullscreen button becomes active after Unity finishes loading.
- The loader uses Unity template variables, so build filenames do not need
  to be edited manually.
- The website button opens https://www.futuredecisions.ai/ in a new tab.
- The page uses only system fonts and local assets.
