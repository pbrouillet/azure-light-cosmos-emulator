# Fluent UI React v9 Migration API Guide

## PACKAGE VERSIONS
- @fluentui/react-components: 9.73.3
- @fluentui/react-icons: Latest
- @griffel/react: ^1.5.32 (CSS-in-JS engine)

---

## CORE PROVIDER & STYLING

### FluentProvider
```ts
import { FluentProvider, PortalMountNodeProvider } from '@fluentui/react-components';

interface FluentProviderProps {
  theme?: PartialTheme;           // Theme object
  dir?: 'ltr' | 'rtl';
  applyStylesToPortals?: boolean; // default: true — SET TO FALSE (see below)
  targetDocument?: Document;      // For SSR
}
```

**CRITICAL: Portal styling bug.** `applyStylesToPortals` must be `false` in this project.
When `true` (default), FluentProvider passes its full Griffel className (including
`background-color: var(--colorNeutralBackground1)`) to every portal mount node. Portal
mount nodes render with `position: absolute; z-index: 1000000`. In Edge/Chromium, an
opaque background on a z-index:1000000 element triggers GPU compositor layer occlusion —
the compositor skips painting lower-z content, making it visually disappear. Setting
`applyStylesToPortals={false}` passes only the CSS variable class (theme tokens) to
portals, not the visual Griffel classes. All portal content components (`DialogSurface`,
`PopoverSurface`, etc.) set their own explicit `background-color` and `color`.

**Portal mount node setup.** Use `PortalMountNodeProvider` to redirect portals inside
`#root` (instead of `document.body`) so they share the same stacking context:
```tsx
function Root() {
  const [portalNode, setPortalNode] = useState<HTMLDivElement | undefined>()
  const portalRef = useCallback((node: HTMLDivElement | null) => {
    if (node) setPortalNode(node)
  }, [])

  return (
    <FluentProvider applyStylesToPortals={false} theme={webDarkTheme}>
      <PortalMountNodeProvider value={portalNode}>
        <App />
      </PortalMountNodeProvider>
      <div ref={portalRef} />
    </FluentProvider>
  )
}
```

### makeStyles (Griffel)
\\\	s
import { makeStyles } from '@griffel/react';
import { tokens } from '@fluentui/react-theme';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    '&:hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    '@media (max-width: 600px)': { flexDirection: 'column' },
  },
});

// In component
const classes = useStyles();
\\\

**Key:** No CSS cascade, no nested selectors, use separate classes.

### Tokens
\\\	s
import { tokens } from '@fluentui/react-theme';

tokens.colorNeutralBackground1
tokens.colorBrandBackground
tokens.colorStatusDangerBackground1
tokens.spacingHorizontalM
tokens.fontSizeBase300
tokens.borderRadiusMedium
\\\

---

## COMPONENT API REFERENCE

### Button & ToggleButton
\\\	s
interface ButtonProps {
  appearance?: 'primary' | 'secondary' | 'outline' | 'subtle' | 'transparent';
  size?: 'small' | 'medium' | 'large';
  shape?: 'rounded' | 'circular' | 'square';
  disabled?: boolean;
  disabledFocusable?: boolean;
  iconPosition?: 'before' | 'after';
  icon?: React.ReactNode;
}

interface ToggleButtonProps extends ButtonProps {
  checked?: boolean;
  defaultChecked?: boolean;
}

<Button appearance='primary'>Click</Button>
<ToggleButton checked={isActive}>Toggle</ToggleButton>
\\\

### Input & Field
\\\	s
interface InputProps {
  appearance?: 'outline' | 'underline' | 'filled-darker' | 'filled-lighter';
  size?: 'small' | 'medium' | 'large';
  type?: 'text' | 'email' | 'password' | 'number' | 'date';
  value?: string;
  defaultValue?: string;
  onChange?: (e, data: { value: string }) => void;
}

interface FieldProps {
  label?: React.ReactNode;
  hint?: React.ReactNode;
  validationState?: 'success' | 'warning' | 'error';
  validationMessage?: React.ReactNode;
  required?: boolean;
}

<Field label='Name'>
  <Input type='text' placeholder='...' />
</Field>
\\\

**Field auto-wires:** aria-labelledby, aria-describedby, aria-required, aria-invalid

### Textarea
\\\	s
interface TextareaProps {
  appearance?: 'outline' | 'underline' | 'filled-darker' | 'filled-lighter';
  size?: 'small' | 'medium' | 'large';
  resize?: 'none' | 'horizontal' | 'vertical' | 'both';
  onChange?: (e, data: { value: string }) => void;
}

<Textarea resize='vertical' placeholder='...' />
\\\

### Spinner
\\\	s
interface SpinnerProps {
  appearance?: 'primary' | 'inverted';
  size?: 'extra-tiny' | 'tiny' | 'extra-small' | 'small' | 'medium' | 'large' | 'extra-large' | 'huge';
  label?: string;
  labelPosition?: 'above' | 'below' | 'before' | 'after';
  delay?: number; // ms before showing
}

<Spinner size='medium' label='Loading...' />
\\\

### Dialog (Modal)
\\\	s
interface DialogProps {
  open?: boolean;
  defaultOpen?: boolean;
  modalType?: 'modal' | 'non-modal' | 'alert';
  inertTrapFocus?: boolean;
  unmountOnClose?: boolean; // default: true
  onOpenChange?: (e, data) => void;
}

<Dialog>
  <DialogTrigger>
    <Button>Open</Button>
  </DialogTrigger>
  <DialogSurface>
    <DialogBody>
      <DialogTitle>Title</DialogTitle>
      <DialogContent>Content</DialogContent>
      <DialogActions>
        <Button>Cancel</Button>
        <Button appearance='primary'>OK</Button>
      </DialogActions>
    </DialogBody>
  </DialogSurface>
</Dialog>

// Non-modal with transparent backdrop
<DialogSurface backdrop={{ appearance: 'transparent' }}>...</DialogSurface>
\\\

### Tree & TreeItem
\\\	s
interface TreeProps {
  openItems?: Iterable<TreeItemValue>;
  onOpenChange?: (e, data) => void;
  onNavigation?: (e, data) => void;
  selectionMode?: 'none' | 'single' | 'multiselect';
  checkedItems?: Iterable<TreeItemValue>;
  onCheckedChange?: (e, data) => void;
  appearance?: 'subtle' | 'subtle-alpha' | 'transparent';
  size?: 'small' | 'medium';
  navigationMode?: 'tree' | 'treegrid';
}

interface TreeItemProps {
  itemType: 'leaf' | 'branch';        // REQUIRED
  value?: string | number;
  parentValue?: string | number;      // For FlatTree
  open?: boolean;
  onOpenChange?: (e, data) => void;
}

interface TreeItemLayoutProps {
  // children = main content
  iconBefore?: React.ReactNode;
  iconAfter?: React.ReactNode;
  expandIcon?: React.ReactNode;
  actions?: React.ReactNode;           // Visible on hover
  aside?: React.ReactNode;             // Badge, etc (always visible)
  selector?: React.ReactNode;          // Checkbox/Radio
}

// NESTED USAGE (recommended for < 1000 items)
<Tree selectionMode='multiselect'>
  <TreeItem itemType='branch' value='item1'>
    <TreeItemLayout>
      Expandable Item
    </TreeItemLayout>
    <Tree>
      <TreeItem itemType='leaf' value='item1-1'>
        <TreeItemLayout>Leaf</TreeItemLayout>
      </TreeItem>
    </Tree>
  </TreeItem>
</Tree>

// FLAT TREE (for virtualization)
import { FlatTree, FlatTreeItem, useFlatTree, flattenTree_unstable } from '@fluentui/react-components';

const items = flattenTree_unstable([
  {
    value: 'item1',
    itemType: 'branch',
    children: <TreeItemLayout>Item 1</TreeItemLayout>,
    subtree: [
      {
        value: 'item1-1',
        itemType: 'leaf',
        children: <TreeItemLayout>Item 1.1</TreeItemLayout>,
      },
    ],
  },
]);

<FlatTree>
  {items.map((item) => (
    <FlatTreeItem key={item.value} {...item}>
      {item.children}
    </FlatTreeItem>
  ))}
</FlatTree>
\\\

**Key TreeItem Rules:**
- itemType is REQUIRED: 'leaf' or 'branch'
- Nested Tree = TreeItem children with subtree
- FlatTree = Flat array + parentValue linking
- For branches: must have children (subtree or nested Tree)
- TreeItemLayout slots: main (auto from children), actions (hover), aside (always visible)

### Card
\\\	s
<Card>
  <CardHeader
    image={<img src='...' />}
    header='Title'
    description='Subtitle'
  />
  <CardFooter action={<Button>Action</Button>} />
</Card>
\\\

### Toolbar
\\\	s
interface ToolbarProps {
  size?: 'small' | 'medium';
  vertical?: boolean;
  checkedValues?: Record<string, string[]>;
  onCheckedValueChange?: (e, data) => void;
}

<Toolbar>
  <ToolbarButton icon={<BoldRegular />}>Bold</ToolbarButton>
  <ToolbarDivider />
  <ToolbarButton>Help</ToolbarButton>
</Toolbar>
\\\

### Divider
\\\	s
interface DividerProps {
  appearance?: 'brand' | 'default' | 'strong' | 'subtle';
  alignContent?: 'start' | 'center' | 'end';
  inset?: boolean;
  vertical?: boolean;
}

<Divider>OR</Divider>
<Divider vertical />
<Divider appearance='strong'>Section</Divider>
\\\

### Text (Typography)
\\\	s
// NO generic <Text> component
// Use preset components only:

import { Display, LargeTitle, Subtitle1, Body1, Body2, Caption1 } from '@fluentui/react-components';

<Display>Heading</Display>
<Body1>Regular text</Body1>
<Caption1>Small text</Caption1>
\\\

### Badge
\\\	s
interface BadgeProps {
  appearance?: 'filled' | 'ghost' | 'outline' | 'tint';
  color?: 'brand' | 'danger' | 'important' | 'informative' | 'severe' | 'subtle' | 'success' | 'warning';
  size?: 'tiny' | 'extra-small' | 'small' | 'medium' | 'large' | 'extra-large';
  shape?: 'circular' | 'rounded' | 'square';
  iconPosition?: 'before' | 'after';
}

interface CounterBadgeProps {
  appearance?: 'filled' | 'ghost';
  count?: number;
  overflowCount?: 99;
  dot?: boolean;
  showZero?: boolean;
}

interface PresenceBadgeProps {
  status?: 'available' | 'busy' | 'away' | 'offline' | 'do-not-disturb' | 'blocked' | 'unknown';
  outOfOffice?: boolean;
}

<Badge color='success'>Active</Badge>
<CounterBadge count={5} />
<PresenceBadge status='available' />
\\\

### Dropdown (Select)
\\\	s
// Simple select (native HTML wrapper)
interface SelectProps {
  appearance?: 'outline' | 'underline' | 'filled-darker' | 'filled-lighter';
  size?: 'small' | 'medium' | 'large';
  value?: string;
  defaultValue?: string;
  onChange?: (e, data: { value: string }) => void;
}

<Select defaultValue='opt1'>
  <option value='opt1'>Option 1</option>
  <option value='opt2'>Option 2</option>
</Select>

// Searchable: use Combobox instead
import { Combobox, Option } from '@fluentui/react-components';

<Combobox placeholder='Search...'>
  <Option>Option A</Option>
  <Option>Option B</Option>
</Combobox>
\\\

### MessageBar
\\\	s
interface MessageBarProps {
  intent?: 'success' | 'warning' | 'error' | 'info';
  layout?: 'singleline' | 'multiline' | 'auto';
}

interface MessageBarActionsProps {
  containerAction?: React.ReactNode; // Usually Dismiss button
}

<MessageBar intent='error'>
  <MessageBarBody>
    <MessageBarTitle>Error</MessageBarTitle>
    Details here
  </MessageBarBody>
  <MessageBarActions containerAction={<Button>Dismiss</Button>} />
</MessageBar>
\\\

---

## ICONS

\\\	s
import { SearchRegular, DeleteFilled, ChevronDownRegular } from '@fluentui/react-icons';

// Icons = named exports: {Name}{Regular|Filled|Color}
// No props needed; size inherited from parent

<Button icon={<SearchRegular />}>Search</Button>
<Badge icon={<DeleteFilled />}>Delete</Badge>
\\\

---

## DataGrid ⚠️ CAUTION

**Recommendation:** Use plain <table> instead.

**Reasons:**
1. Limited styling flexibility
2. Performance issues with large datasets
3. Minimal tree/nested row support
4. Complex API, hard to customize

**Safer Alternative:**
\\\	s
<table style={{ width: '100%', borderCollapse: 'collapse' }}>
  <thead>
    <tr><th>Column 1</th><th>Column 2</th></tr>
  </thead>
  <tbody>
    {items.map(item => (
      <tr key={item.id}>
        <td>{item.col1}</td>
        <td>{item.col2}</td>
      </tr>
    ))}
  </tbody>
</table>
\\\

Use makeStyles to style tr/td with Fluent tokens.

---

## MONACO EDITOR THEME SWITCHING

\\\	s
import { teamsLightTheme, teamsDarkTheme } from '@fluentui/react-theme';
import * as monaco from 'monaco-editor';

function MonacoWithFluentTheme({ isDark }) {
  const theme = isDark ? teamsDarkTheme : teamsLightTheme;
  
  useEffect(() => {
    const monacoTheme = {
      base: isDark ? 'vs-dark' : 'vs',
      inherit: true,
      rules: [
        {
          token: 'keyword',
          foreground: theme.colorBrandForeground1?.replace('#', ''),
        },
        {
          token: 'string',
          foreground: theme.colorStatusSuccessForeground1?.replace('#', ''),
        },
      ],
      colors: {
        'editor.background': theme.colorNeutralBackground1,
        'editor.foreground': theme.colorNeutralForeground1,
        'editorCursor.foreground': theme.colorBrandForeground1,
      },
    };
    
    monaco.editor.defineTheme('fluent-theme', monacoTheme);
    monaco.editor.setTheme('fluent-theme');
  }, [isDark, theme]);

  return <div style={{ height: '600px' }} />;
}
\\\

**Key:**
- Extract hex from Fluent tokens (remove '#')
- Use \ditor.setTheme()\ for dynamic switching
- Map syntax highlight rules (keyword, string, etc) to Fluent colors
- Monaco requires base: 'vs' | 'vs-dark' for fallback

---

## BUILD-RELEVANT CAVEATS

1. **Griffel Compilation**
   - makeStyles processed by Griffel during build (wyw-in-js)
   - Build must handle @griffel/tag-processor (webpack/vite plugin)
   - Styles injected at runtime; no separate CSS file

2. **Tree Complexity**
   - Use nested Tree for < 1000 items
   - Use FlatTree + createHeadlessTree() for virtualization
   - Flat tree requires aria-level, aria-posinset, aria-setsize

3. **Dialog Focus Management**
   - Dialogs auto-trap focus within DialogSurface
   - inertTrapFocus={true} = strict HTML dialog spec
   - unmountOnClose={true} (default) preserves state when closed=false

4. **Slot Consistency**
   - All components = ForwardRefComponent (use ref)
   - Pass ref to root slot, other props to specific slots
   - No custom className merging; use makeStyles() for styling

5. **No Icon Props**
   - Icons don't accept size/color props
   - Size inherited from parent context
   - Don't override with CSS

6. **Text Typography**
   - NO generic <Text> component
   - Use preset components: Display, Body1, Caption1, etc.
   - Custom sizes: use makeStyles + tokens

7. **Field Integration**
   - Field manages aria-labelledby, aria-describedby, aria-invalid
   - Form controls = direct children or render-function children
   - Size, required, validationState flow via context

8. **Theme Persistence**
   - Wrap entire app in <FluentProvider theme={theme}>
   - Theme changes = re-render provider
   - Tokens = CSS variables in <style> tag by FluentProvider

---

## AVAILABLE THEMES

From @fluentui/tokens:
- teamsLightTheme, teamsLightV21Theme
- teamsDarkTheme, teamsDarkV21Theme
- teamsHighContrastTheme
- webLightTheme, webDarkTheme
- Custom: createLightTheme(), createDarkTheme(), createHighContrastTheme()

---

## IMPORT CHECKLIST

\\\	s
// Core
import { FluentProvider } from '@fluentui/react-components';
import { teamsLightTheme, tokens } from '@fluentui/react-theme';

// Styling
import { makeStyles } from '@griffel/react';

// Forms
import { Input, Textarea, Field, Select } from '@fluentui/react-components';

// Interactive
import { Button, ToggleButton } from '@fluentui/react-components';
import { Dialog, DialogSurface, DialogBody, DialogTitle, DialogContent, DialogActions, DialogTrigger } from '@fluentui/react-components';

// Containers
import { Card, CardHeader, CardFooter } from '@fluentui/react-components';
import { Toolbar, ToolbarButton, ToolbarDivider } from '@fluentui/react-components';

// Data
import { Tree, TreeItem, TreeItemLayout } from '@fluentui/react-components';
import { FlatTree, FlatTreeItem, flattenTree_unstable } from '@fluentui/react-components';

// Display
import { Badge, CounterBadge, PresenceBadge } from '@fluentui/react-components';
import { Divider } from '@fluentui/react-components';
import { Spinner } from '@fluentui/react-components';
import { MessageBar, MessageBarBody, MessageBarTitle, MessageBarActions } from '@fluentui/react-components';
import { Display, LargeTitle, Subtitle1, Body1, Body2, Caption1 } from '@fluentui/react-components';

// Icons
import { SearchRegular, ChevronDownRegular, DeleteFilled } from '@fluentui/react-icons';
\\\

All components support: ref forwarding, standard React events, data-* attributes.
