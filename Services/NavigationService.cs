using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WPFStandardFramework.Common.Navigation;
using WPFStandardFramework.Interfaces;

namespace WPFStandardFramework.Services
{
    /// <summary>
    /// 区域化导航服务实现 - 内存安全优化版
    /// </summary>
    public class NavigationService : INavigationService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<NavigationService> _logger;

        // 使用弱引用缓存，避免内存泄漏
        private readonly ConcurrentDictionary<Type, WeakReference<FrameworkElement>> _viewCache = new();

        // 使用AsyncLocal处理并发导航
        private readonly AsyncLocal<bool> _isNavigating = new();

        // 添加取消令牌支持
        private CancellationTokenSource? _navigationCts;

        // 区域存储（使用弱引用，避免阻止控件回收）
        private readonly ConcurrentDictionary<string, WeakReference<ContentControl>> _regions = new();

        // 导航历史（使用轻量级条目，不持有View引用）
        private readonly ConcurrentDictionary<string, Stack<NavigationHistoryEntry>> _navigationHistory = new();

        // 当前视图的弱引用
        private readonly ConcurrentDictionary<string, WeakReference<FrameworkElement>> _currentViews = new();

        // 手动视图-ViewModel映射
        private readonly ConcurrentDictionary<Type, Type> _viewModelMappings = new();

        // 导航拦截器
        private readonly List<INavigationInterceptor> _interceptors = new();

        // 属性实现 - 检查任何区域是否有历史记录
        public bool CanGoBackAny => _navigationHistory.Values.Any(stack => stack.Count > 0);

        public event EventHandler<NavigationEventArgs>? Navigated;
        public event EventHandler<NavigationStateChangedEventArgs>? NavigationStateChanged;

        public NavigationService(IServiceProvider serviceProvider, ILogger<NavigationService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// 注册导航区域（使用弱引用）
        /// </summary>
        public void RegisterRegion(string regionName, ContentControl contentControl)
        {
            if (string.IsNullOrWhiteSpace(regionName))
                throw new ArgumentNullException(nameof(regionName));

            // 确保在UI线程操作
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => RegisterRegion(regionName, contentControl));
                return;
            }

            _regions[regionName] = new WeakReference<ContentControl>(contentControl);
            _navigationHistory.TryAdd(regionName, new Stack<NavigationHistoryEntry>());

            // 监听区域卸载事件
            contentControl.Unloaded += (s, e) =>
            {
                // 区域卸载时清理相关资源
                CleanupRegion(regionName);
            };

            _logger.LogDebug("区域已注册：{RegionName}", regionName);
        }

        /// <summary>
        /// 清理区域资源
        /// </summary>
        private void CleanupRegion(string regionName)
        {
            _regions.TryRemove(regionName, out _);
            _currentViews.TryRemove(regionName, out _);

            if (_navigationHistory.TryRemove(regionName, out var stack))
            {
                stack.Clear();
            }

            _logger.LogDebug("区域资源已清理：{RegionName}", regionName);
        }

        /// <summary>
        /// 注册视图-ViewModel映射
        /// </summary>
        public void RegisterViewMapping<TView, TViewModel>() where TView : FrameworkElement
        {
            _viewModelMappings[typeof(TView)] = typeof(TViewModel);
            _logger.LogDebug("视图映射注册：{ViewType} -> {ViewModelType}", typeof(TView).Name, typeof(TViewModel).Name);
        }

        /// <summary>
        /// 注册导航拦截器
        /// </summary>
        public void RegisterInterceptor(INavigationInterceptor interceptor)
        {
            if (!_interceptors.Contains(interceptor))
            {
                _interceptors.Add(interceptor);
                _logger.LogDebug("导航拦截器注册：{InterceptorType}", interceptor.GetType().Name);
            }
        }

        /// <summary>
        /// 移除导航拦截器
        /// </summary>
        public void RemoveInterceptor(INavigationInterceptor interceptor)
        {
            _interceptors.Remove(interceptor);
        }

        /// <summary>
        /// 导航到指定视图（默认Main区域）
        /// </summary>
        public async Task NavigateToAsync<TView>(object? parameter = null) where TView : FrameworkElement
        {
            await NavigateToAsync(typeof(TView), RegionNames.Main, parameter, CancellationToken.None);
        }

        /// <summary>
        /// 导航到指定区域的视图
        /// </summary>
        public async Task NavigateToAsync<TView>(string regionName, object? parameter = null) where TView : FrameworkElement
        {
            await NavigateToAsync(typeof(TView), regionName, parameter, CancellationToken.None);
        }

        /// <summary>
        /// 导航到指定视图类型
        /// </summary>
        public async Task NavigateToAsync(Type viewType, string regionName, object? parameter = null)
        {
            await NavigateToAsync(viewType, regionName, parameter, CancellationToken.None);
        }

        /// <summary>
        /// 导航到指定视图类型（带取消支持）
        /// </summary>
        public async Task NavigateToAsync(Type viewType, string regionName, object? parameter = null,
            CancellationToken cancellationToken = default)
        {
            // 取消之前的导航
            _navigationCts?.Cancel();
            _navigationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var currentToken = _navigationCts.Token;

            if (_isNavigating.Value)
            {
                _logger.LogWarning("导航正在进行中，等待完成");
                // 等待一小段时间
                try
                {
                    await Task.Delay(100, currentToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_isNavigating.Value)
                {
                    _logger.LogWarning("导航仍在进行中，忽略重复请求");
                    return;
                }
            }

            try
            {
                _isNavigating.Value = true;
                currentToken.ThrowIfCancellationRequested();

                OnNavigationStateChanged();

                // 获取区域（带验证）
                if (!TryGetRegion(regionName, out var region) || region == null)
                {
                    throw new InvalidOperationException($"区域 '{regionName}' 未注册或已被释放");
                }

                // 执行拦截器检查（支持取消）
                foreach (var interceptor in _interceptors.ToList()) // 创建副本避免迭代时修改
                {
                    currentToken.ThrowIfCancellationRequested();

                    if (!await interceptor.CanNavigateAsync(regionName, viewType, parameter))
                    {
                        _logger.LogWarning("导航被拦截器 {Interceptor} 阻止", interceptor.GetType().Name);
                        return;
                    }
                    await interceptor.OnNavigatingAsync(regionName, viewType, parameter);
                }

                _logger.LogInformation("开始导航到区域 {RegionName} 的视图 {ViewType}", regionName, viewType.Name);

                // 获取或创建视图（带取消支持）
                var view = await GetOrCreateViewAsync(viewType, currentToken);
                currentToken.ThrowIfCancellationRequested();

                // 处理当前视图的离开逻辑
                await HandleCurrentViewLeavingAsync(regionName, parameter, currentToken);

                // 设置ViewModel并触发导航事件
                await SetupViewModelAsync(view, parameter, currentToken);
                currentToken.ThrowIfCancellationRequested();

                // 更新区域内容（UI线程）
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    region.Content = view;
                });

                // 更新当前视图引用
                _currentViews[regionName] = new WeakReference<FrameworkElement>(view);

                // 触发导航完成事件
                var eventArgs = new NavigationEventArgs(regionName, viewType, parameter);
                Navigated?.Invoke(this, eventArgs);

                // 通知拦截器导航完成
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.OnNavigatedAsync(regionName, viewType, parameter, true);
                }

                _logger.LogInformation("导航完成到区域 {RegionName} 的视图 {ViewType}", regionName, viewType.Name);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("导航到区域 {RegionName} 的视图 {ViewType} 被取消", regionName, viewType.Name);

                // 通知拦截器导航取消
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.OnNavigatedAsync(regionName, viewType, parameter, false);
                }
            }
            catch (NavigationCanceledException ex)
            {
                _logger.LogInformation("用户取消导航：{Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导航到区域 {RegionName} 的视图 {ViewType} 失败", regionName, viewType.Name);

                // 通知拦截器导航失败
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.OnNavigatedAsync(regionName, viewType, parameter, false);
                }

                // 显示友好错误提示
                await ShowNavigationErrorAsync(viewType, ex);
            }
            finally
            {
                _isNavigating.Value = false;
                OnNavigationStateChanged();
            }
        }

        /// <summary>
        /// 获取或创建视图（内存安全版）
        /// </summary>
        private async Task<FrameworkElement> GetOrCreateViewAsync(Type viewType, CancellationToken cancellationToken)
        {
            // 检查缓存策略
            var cachePolicy = viewType.GetCustomAttribute<ViewCachePolicyAttribute>()?.Policy ?? ViewCachePolicy.Transient;

            switch (cachePolicy)
            {
                case ViewCachePolicy.Cached:
                    // 使用弱引用缓存
                    if (_viewCache.TryGetValue(viewType, out var weakRef) &&
                        weakRef.TryGetTarget(out var cachedView))
                    {
                        _logger.LogDebug("使用缓存视图：{ViewType}", viewType.Name);
                        return cachedView;
                    }

                    // 创建新视图并缓存弱引用
                    var newView = await CreateViewAsync(viewType, cancellationToken);
                    _viewCache[viewType] = new WeakReference<FrameworkElement>(newView);
                    return newView;

                case ViewCachePolicy.Transient:
                default:
                    // 每次都创建新实例
                    _logger.LogDebug("创建临时视图：{ViewType}", viewType.Name);
                    return await CreateViewAsync(viewType, cancellationToken);
            }
        }

        /// <summary>
        /// 创建视图（自动处理UI线程切换）
        /// </summary>
        private async Task<FrameworkElement> CreateViewAsync(Type viewType, CancellationToken cancellationToken)
        {
            // 如果不在 UI 线程，先切换到 UI 线程
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                return await Application.Current.Dispatcher.InvokeAsync(() =>
                    CreateViewCore(viewType, cancellationToken)).Task;
            }

            // 已经在 UI 线程，直接执行
            return CreateViewCore(viewType, cancellationToken);
        }

        /// <summary>
        /// 核心视图创建逻辑（必须在 UI 线程调用）
        /// </summary>
        private FrameworkElement CreateViewCore(Type viewType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.LogDebug("创建视图：{ViewType}", viewType.Name);
                return (FrameworkElement)_serviceProvider.GetRequiredService(viewType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建视图 {ViewType} 失败", viewType.Name);
                throw;
            }
        }

        /// <summary>
        /// 处理当前视图离开逻辑
        /// </summary>
        private async Task HandleCurrentViewLeavingAsync(string regionName, object? parameter, CancellationToken cancellationToken)
        {
            if (_currentViews.TryGetValue(regionName, out var weakRef) &&
                weakRef.TryGetTarget(out var currentView))
            {
                // 询问是否可以离开
                if (currentView.DataContext is INavigationAware navigationAware)
                {
                    if (!await navigationAware.CanNavigateFromAsync())
                    {
                        throw new NavigationCanceledException("用户取消了导航");
                    }

                    await navigationAware.OnNavigatedFromAsync();
                }

                // 保存历史记录（使用条目而不是完整视图）
                var historyEntry = await CreateHistoryEntryAsync(regionName, currentView, parameter, cancellationToken);
                if (historyEntry != null)
                {
                    var stack = _navigationHistory.GetOrAdd(regionName, _ => new Stack<NavigationHistoryEntry>());
                    stack.Push(historyEntry);

                    // 限制历史栈大小
                    const int MAX_HISTORY_SIZE = 20;
                    while (stack.Count > MAX_HISTORY_SIZE)
                    {
                        stack.Pop();
                    }
                }
            }
        }

        /// <summary>
        /// 创建历史记录条目（不持有View引用）
        /// </summary>
        private async Task<NavigationHistoryEntry?> CreateHistoryEntryAsync(
            string regionName,
            FrameworkElement view,
            object? parameter,
            CancellationToken cancellationToken)
        {
            var entry = new NavigationHistoryEntry
            {
                RegionName = regionName,
                ViewType = view.GetType(),
                ViewModelType = view.DataContext?.GetType(),
                Parameter = parameter,
                Timestamp = DateTime.UtcNow
            };

            // 保存ViewModel状态（如果需要）
            if (view.DataContext is IStatefulViewModel stateful)
            {
                var state = new Dictionary<string, object>();
                await stateful.SaveStateAsync(state);
                entry.ViewModelState = state;
            }

            return entry;
        }


        /// <summary>
        /// 设置ViewModel（支持多种定位方式）
        /// </summary>
        private async Task SetupViewModelAsync(FrameworkElement view, object? parameter, CancellationToken cancellationToken)
        {
            Type? viewModelType = null;

            _logger.LogInformation("========== SetupViewModelAsync 开始 ==========");
            _logger.LogInformation("视图类型: {ViewType}", view.GetType().FullName);
            _logger.LogInformation("当前 DataContext: {DataContext}", view.DataContext?.GetType().FullName ?? "null");

            // ⭐ 如果已经有 DataContext，直接使用它
            if (view.DataContext != null)
            {
                _logger.LogInformation("✅ 视图已有 DataContext: {ViewModelType}，直接使用", view.DataContext.GetType().FullName);

                var existingViewModel = view.DataContext;

                // 如果实现了 INavigationAware，调用导航方法
                if (existingViewModel is INavigationAware existingNavAware)
                {
                    _logger.LogInformation("🎯 调用现有 ViewModel 的 OnNavigatedToAsync...");
                    await existingNavAware.OnNavigatedToAsync(parameter);
                    _logger.LogInformation("✅ OnNavigatedToAsync 调用完成");
                }
                else
                {
                    _logger.LogWarning("⚠️ 现有 ViewModel {ViewModelType} 未实现 INavigationAware", existingViewModel.GetType().Name);
                }

                _logger.LogInformation("========== SetupViewModelAsync 完成 ==========");
                return;
            }

            // 1. 检查特性标记
            var navigationAttribute = view.GetType().GetCustomAttribute<NavigationViewModelAttribute>();
            if (navigationAttribute != null)
            {
                viewModelType = navigationAttribute.ViewModelType;
                _logger.LogInformation("✅ 通过特性找到 ViewModel 类型: {ViewModelType}", viewModelType?.FullName);
            }
            else
            {
                _logger.LogWarning("❌ 未找到 NavigationViewModelAttribute");
            }

            // 2. 检查手动映射配置
            if (viewModelType == null && _viewModelMappings.TryGetValue(view.GetType(), out var mappedType))
            {
                viewModelType = mappedType;
                _logger.LogInformation("✅ 通过映射找到 ViewModel 类型: {ViewModelType}", viewModelType?.FullName);
            }

            // 3. 命名约定
            if (viewModelType == null)
            {
                viewModelType = FindViewModelByConvention(view.GetType());
                if (viewModelType != null)
                {
                    _logger.LogInformation("✅ 通过命名约定找到 ViewModel 类型: {ViewModelType}", viewModelType?.FullName);
                }
                else
                {
                    _logger.LogWarning("❌ 通过命名约定未找到 ViewModel 类型");
                }
            }

            if (viewModelType == null)
            {
                _logger.LogError("❌ 无法为视图 {ViewType} 找到 ViewModel 类型，导航将不会触发 OnNavigatedToAsync", view.GetType().Name);
                return;
            }

            _logger.LogInformation("开始创建 ViewModel 实例: {ViewModelType}", viewModelType.FullName);

            // 创建并设置ViewModel
            var viewModel = _serviceProvider.GetRequiredService(viewModelType);
            view.DataContext = viewModel;
            _logger.LogInformation("✅ 视图 {ViewType} 已绑定 ViewModel {ViewModelType} (实例Hash: {HashCode})",
                view.GetType().Name, viewModelType.Name, viewModel.GetHashCode());

            // 如果ViewModel实现了INavigationAware，调用导航方法
            if (viewModel is INavigationAware newNavAware)
            {
                _logger.LogInformation("🎯 ViewModel 实现了 INavigationAware，开始调用 OnNavigatedToAsync...");
                await newNavAware.OnNavigatedToAsync(parameter);
                _logger.LogInformation("✅ OnNavigatedToAsync 调用完成");
            }
            else
            {
                _logger.LogWarning("⚠️ ViewModel {ViewModelType} 未实现 INavigationAware", viewModelType.Name);
            }

            _logger.LogInformation("========== SetupViewModelAsync 完成 ==========");
        }

        private Type? FindViewModelByConvention(Type viewType)
        {
            var viewModelName = viewType.FullName?.Replace("Views", "ViewModels").Replace("View", "ViewModel");
            return !string.IsNullOrEmpty(viewModelName) ? Type.GetType(viewModelName) : null;
        }

        /// <summary>
        /// 安全获取区域
        /// </summary>
        private bool TryGetRegion(string regionName, out ContentControl? region)
        {
            region = null;

            if (!_regions.TryGetValue(regionName, out var weakRef))
                return false;

            if (weakRef.TryGetTarget(out var target))
            {
                region = target;
                return true;
            }

            // 清理已释放的引用
            _regions.TryRemove(regionName, out _);
            return false;
        }

        /// <summary>
        /// 后退导航
        /// </summary>
        public async Task<bool> GoBackAsync()
        {
            return await GoBackAsync(RegionNames.Main, CancellationToken.None);
        }

        /// <summary>
        /// 从指定区域后退
        /// </summary>
        public async Task<bool> GoBackAsync(string regionName)
        {
            return await GoBackAsync(regionName, CancellationToken.None);
        }

        /// <summary>
        /// 从指定区域后退（带取消支持）
        /// </summary>
        public async Task<bool> GoBackAsync(string regionName, CancellationToken cancellationToken)
        {
            if (!CanGoBack(regionName))
            {
                _logger.LogWarning("区域 {RegionName} 无法后退", regionName);
                return false;
            }

            if (!TryGetRegion(regionName, out var region))
            {
                _logger.LogWarning("区域 {RegionName} 未注册", regionName);
                return false;
            }

            try
            {
                _isNavigating.Value = true;
                OnNavigationStateChanged();

                var stack = _navigationHistory[regionName];
                var historyEntry = stack.Pop();

                _logger.LogInformation("后退导航到区域 {RegionName} 的视图 {ViewType}", regionName, historyEntry.ViewType.Name);

                // 重新创建视图
                var view = await GetOrCreateViewAsync(historyEntry.ViewType, cancellationToken);

                // 恢复ViewModel
                if (historyEntry.ViewModelType != null)
                {
                    var viewModel = _serviceProvider.GetRequiredService(historyEntry.ViewModelType);
                    view.DataContext = viewModel;

                    // 恢复状态
                    if (historyEntry.ViewModelState != null && viewModel is IStatefulViewModel stateful)
                    {
                        await stateful.RestoreStateAsync(historyEntry.ViewModelState);
                    }

                    // 通知ViewModel导航回来
                    if (viewModel is INavigationAware navigationAware)
                    {
                        await navigationAware.OnNavigatedToAsync(historyEntry.Parameter);
                    }
                }

                // 更新区域内容（UI线程）
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    region.Content = view;
                });

                _currentViews[regionName] = new WeakReference<FrameworkElement>(view);

                // 触发导航事件
                var eventArgs = new NavigationEventArgs(regionName, historyEntry.ViewType, historyEntry.Parameter);
                Navigated?.Invoke(this, eventArgs);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后退导航失败");
                return false;
            }
            finally
            {
                _isNavigating.Value = false;
                OnNavigationStateChanged();
            }
        }

        /// <summary>
        /// 检查指定区域是否可以后退
        /// </summary>
        public bool CanGoBack(string regionName)
        {
            return _navigationHistory.TryGetValue(regionName, out var stack) && stack.Count > 0;
        }

        /// <summary>
        /// 清除所有导航历史
        /// </summary>
        public void ClearAllNavigationHistory()
        {
            foreach (var stack in _navigationHistory.Values)
            {
                stack.Clear();
            }
            _logger.LogInformation("已清除所有导航历史");
            OnNavigationStateChanged();
        }

        /// <summary>
        /// 清除指定区域的导航历史
        /// </summary>
        public void ClearNavigationHistory(string regionName)
        {
            if (_navigationHistory.TryGetValue(regionName, out var stack))
            {
                stack.Clear();
                _logger.LogInformation("已清除区域 {RegionName} 的导航历史", regionName);
                OnNavigationStateChanged();
            }
        }

        /// <summary>
        /// 显示导航错误
        /// </summary>
        private async Task ShowNavigationErrorAsync(Type viewType, Exception ex)
        {
            try
            {
                var notificationService = _serviceProvider.GetService<INotificationService>();
                if (notificationService != null)
                {
                    await Task.Run(() =>
                    {
                        notificationService.ShowError(
                            $"导航到 {viewType.Name} 失败：{ex.Message}",
                            "导航错误");
                    });
                }
            }
            catch
            {
                // 忽略通知服务的错误
            }
        }

        private void OnNavigationStateChanged()
        {
            var totalStackCount = _navigationHistory.Values.Sum(stack => stack.Count);
            NavigationStateChanged?.Invoke(this, new NavigationStateChangedEventArgs(
                _isNavigating.Value,
                CanGoBackAny,
                totalStackCount
            ));
        }

        /// <summary>
        /// 资源释放
        /// </summary>
        public void Dispose()
        {
            _navigationCts?.Cancel();
            _navigationCts?.Dispose();

            // 清理所有区域
            foreach (var regionName in _regions.Keys.ToList())
            {
                CleanupRegion(regionName);
            }

            // 清空缓存
            _viewCache.Clear();

            GC.SuppressFinalize(this);
        }
    }
}