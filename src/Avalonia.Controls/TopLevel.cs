// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Avalonia.Controls
{
    /// <summary>
    /// Base class for top-level widgets.
    /// </summary>
    /// <remarks>
    /// This class acts as a base for top level widget.
    /// It handles scheduling layout, styling and rendering as well as
    /// tracking the widget's <see cref="ClientSize"/>.
    /// </remarks>
    public abstract class TopLevel : ContentControl, IInputRoot, ILayoutRoot, IRenderRoot, ICloseable, IStyleRoot
    {
        /// <summary>
        /// Defines the <see cref="ClientSize"/> property.
        /// </summary>
        public static readonly DirectProperty<TopLevel, Size> ClientSizeProperty =
            AvaloniaProperty.RegisterDirect<TopLevel, Size>(nameof(ClientSize), o => o.ClientSize);

        /// <summary>
        /// Defines the <see cref="IInputRoot.PointerOverElement"/> property.
        /// </summary>
        public static readonly StyledProperty<IInputElement> PointerOverElementProperty =
            AvaloniaProperty.Register<TopLevel, IInputElement>(nameof(IInputRoot.PointerOverElement));

        private readonly IInputManager _inputManager;
        private readonly IAccessKeyHandler _accessKeyHandler;
        private readonly IKeyboardNavigationHandler _keyboardNavigationHandler;
        private readonly IApplicationLifecycle _applicationLifecycle;
        private readonly IPlatformRenderInterface _renderInterface;
        private Size _clientSize;

        /// <summary>
        /// Initializes static members of the <see cref="TopLevel"/> class.
        /// </summary>
        static TopLevel()
        {
            AffectsMeasure(ClientSizeProperty);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevel"/> class.
        /// </summary>
        /// <param name="impl">The platform-specific window implementation.</param>
        public TopLevel(ITopLevelImpl impl)
            : this(impl, AvaloniaLocator.Current)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevel"/> class.
        /// </summary>
        /// <param name="impl">The platform-specific window implementation.</param>
        /// <param name="dependencyResolver">
        /// The dependency resolver to use. If null the default dependency resolver will be used.
        /// </param>
        public TopLevel(ITopLevelImpl impl, IAvaloniaDependencyResolver dependencyResolver)
        {
            if (impl == null)
            {
                throw new InvalidOperationException(
                    "Could not create window implementation: maybe no windowing subsystem was initialized?");
            }

            PlatformImpl = impl;
            dependencyResolver = dependencyResolver ?? AvaloniaLocator.Current;
            var styler = TryGetService<IStyler>(dependencyResolver);

            _accessKeyHandler = TryGetService<IAccessKeyHandler>(dependencyResolver);
            _inputManager = TryGetService<IInputManager>(dependencyResolver);
            _keyboardNavigationHandler = TryGetService<IKeyboardNavigationHandler>(dependencyResolver);
            _applicationLifecycle = TryGetService<IApplicationLifecycle>(dependencyResolver);
            _renderInterface = TryGetService<IPlatformRenderInterface>(dependencyResolver);

            var renderLoop = TryGetService<IRenderLoop>(dependencyResolver);
            var rendererFactory = TryGetService<IRendererFactory>(dependencyResolver);
            Renderer = rendererFactory?.CreateRenderer(this, renderLoop);

            PlatformImpl.SetInputRoot(this);

            PlatformImpl.Closed = HandleClosed;
            PlatformImpl.Input = HandleInput;
            PlatformImpl.Paint = HandlePaint;
            PlatformImpl.Resized = HandleResized;
            PlatformImpl.ScalingChanged = HandleScalingChanged;


            _keyboardNavigationHandler?.SetOwner(this);
            _accessKeyHandler?.SetOwner(this);
            styler?.ApplyStyles(this);

            ClientSize = PlatformImpl.ClientSize;
            
            this.GetObservable(PointerOverElementProperty)
                .Select(
                    x => (x as InputElement)?.GetObservable(CursorProperty) ?? Observable.Empty<Cursor>())
                .Switch().Subscribe(cursor => PlatformImpl.SetCursor(cursor?.PlatformCursor));

            if (_applicationLifecycle != null)
            {
                _applicationLifecycle.OnExit += OnApplicationExiting;
            }
        }

        /// <summary>
        /// Fired when the window is closed.
        /// </summary>
        public event EventHandler Closed;

        /// <summary>
        /// Gets or sets the client size of the window.
        /// </summary>
        public Size ClientSize
        {
            get { return _clientSize; }
            protected set { SetAndRaise(ClientSizeProperty, ref _clientSize, value); }
        }

        /// <summary>
        /// Gets the platform-specific window implementation.
        /// </summary>
        public ITopLevelImpl PlatformImpl
        {
            get;
        }
        
        /// <summary>
        /// Gets the renderer for the window.
        /// </summary>
        public IRenderer Renderer { get; private set; }

        /// <summary>
        /// Gets the access key handler for the window.
        /// </summary>
        IAccessKeyHandler IInputRoot.AccessKeyHandler => _accessKeyHandler;

        /// <summary>
        /// Gets or sets the keyboard navigation handler for the window.
        /// </summary>
        IKeyboardNavigationHandler IInputRoot.KeyboardNavigationHandler => _keyboardNavigationHandler;

        /// <summary>
        /// Gets or sets the input element that the pointer is currently over.
        /// </summary>
        IInputElement IInputRoot.PointerOverElement
        {
            get { return GetValue(PointerOverElementProperty); }
            set { SetValue(PointerOverElementProperty, value); }
        }

        /// <summary>
        /// Gets or sets a value indicating whether access keys are shown in the window.
        /// </summary>
        bool IInputRoot.ShowAccessKeys
        {
            get { return GetValue(AccessText.ShowAccessKeyProperty); }
            set { SetValue(AccessText.ShowAccessKeyProperty, value); }
        }

        /// <inheritdoc/>
        Size ILayoutRoot.MaxClientSize => Size.Infinity;

        /// <inheritdoc/>
        double ILayoutRoot.LayoutScaling => PlatformImpl.Scaling;

        IStyleHost IStyleHost.StylingParent
        {
            get { return AvaloniaLocator.Current.GetService<IGlobalStyles>(); }
        }

        /// <inheritdoc/>
        IRenderTarget IRenderRoot.CreateRenderTarget()
        {
            return _renderInterface.CreateRenderTarget(PlatformImpl.Surfaces);
        }

        /// <inheritdoc/>
        void IRenderRoot.Invalidate(Rect rect)
        {
            PlatformImpl.Invalidate(rect);
        }

        /// <inheritdoc/>
        Point IRenderRoot.PointToClient(Point p)
        {
            return PlatformImpl.PointToClient(p);
        }

        /// <inheritdoc/>
        Point IRenderRoot.PointToScreen(Point p)
        {
            return PlatformImpl.PointToScreen(p);
        }

        /// <summary>
        /// Handles a paint notification from <see cref="ITopLevelImpl.Resized"/>.
        /// </summary>
        /// <param name="rect">The dirty area.</param>
        protected virtual void HandlePaint(Rect rect)
        {
            Renderer?.Paint(rect);
        }

        /// <summary>
        /// Handles a closed notification from <see cref="ITopLevelImpl.Closed"/>.
        /// </summary>
        protected virtual void HandleClosed()
        {
            Closed?.Invoke(this, EventArgs.Empty);
            Renderer?.Dispose();
            Renderer = null;
            _applicationLifecycle.OnExit -= OnApplicationExiting;
        }

        /// <summary>
        /// Handles a resize notification from <see cref="ITopLevelImpl.Resized"/>.
        /// </summary>
        /// <param name="clientSize">The new client size.</param>
        protected virtual void HandleResized(Size clientSize)
        {
            ClientSize = clientSize;
            Width = clientSize.Width;
            Height = clientSize.Height;
            LayoutManager.Instance.ExecuteLayoutPass();
            Renderer?.Resized(clientSize);
        }

        /// <summary>
        /// Handles a window scaling change notification from 
        /// <see cref="ITopLevelImpl.ScalingChanged"/>.
        /// </summary>
        /// <param name="scaling">The window scaling.</param>
        protected virtual void HandleScalingChanged(double scaling)
        {
            foreach (ILayoutable control in this.GetSelfAndVisualDescendents())
            {
                control.InvalidateMeasure();
            }
        }

        /// <inheritdoc/>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            throw new InvalidOperationException(
                $"Control '{GetType().Name}' is a top level control and cannot be added as a child.");
        }

        /// <summary>
        /// Tries to get a service from an <see cref="IAvaloniaDependencyResolver"/>, logging a
        /// warning if not found.
        /// </summary>
        /// <typeparam name="T">The service type.</typeparam>
        /// <param name="resolver">The resolver.</param>
        /// <returns>The service.</returns>
        private T TryGetService<T>(IAvaloniaDependencyResolver resolver) where T : class
        {
            var result = resolver.GetService<T>();

            if (result == null)
            {
                Logger.Warning(
                    LogArea.Control,
                    this,
                    "Could not create {Service} : maybe Application.RegisterServices() wasn't called?",
                    typeof(T));
            }

            return result;
        }

        private void OnApplicationExiting(object sender, EventArgs args)
        {
            HandleApplicationExiting();
        }

        /// <summary>
        /// Handles the application exiting, either from the last window closing, or a call to <see cref="IApplicationLifecycle.Exit"/>.
        /// </summary>
        protected virtual void HandleApplicationExiting()
        {
        }

        /// <summary>
        /// Handles input from <see cref="ITopLevelImpl.Input"/>.
        /// </summary>
        /// <param name="e">The event args.</param>
        private void HandleInput(RawInputEventArgs e)
        {
            _inputManager.ProcessInput(e);
        }
    }
}
