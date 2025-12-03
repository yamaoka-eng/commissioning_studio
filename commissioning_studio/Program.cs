using AntDesign;
using commissioning_studio.Components;
using commissioning_studio.Ecal;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 注册 Ant Design Blazor 服务
builder.Services.AddAntDesign();

// 注入 eCAL 服务单例（保持已有）
builder.Services.AddSingleton<EcalService>();

var app = builder.Build();

// 启动 eCAL 服务（在 app.Build() 后，app.Run() 前执行一次）
var ecal = app.Services.GetRequiredService<EcalService>();
// 尝试启动；若抛异常请在日志中查看原因（例如 eCAL 本地库缺失）
ecal.Start();

// 在应用停止时优雅停止 eCAL 服务
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        ecal.Stop();
    }
    catch
    {
        // 忽略停止异常，必要时记录日志
    }
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
