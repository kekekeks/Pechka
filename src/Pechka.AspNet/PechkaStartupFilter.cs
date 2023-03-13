using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CoreRPC;
using CoreRPC.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Pechka.AspNet.Database;

namespace Pechka.AspNet;

class PechkaStartupFilter : IStartupFilter
{
    private readonly RuntimeAppInfo _info;
    private readonly TsInterop _interop;
    private readonly IEnumerable<IMethodCallInterceptor> _interceptors;
    private readonly PechkaMigrationInternalConfiguration _migrationsConfig;

    public PechkaStartupFilter(RuntimeAppInfo info, TsInterop interop, 
        IEnumerable<IMethodCallInterceptor> interceptors, PechkaMigrationInternalConfiguration migrationsConfig)
    {
        _info = info;
        _interop = interop;
        _interceptors = interceptors;
        _migrationsConfig = migrationsConfig;
    }
    
    void StaticFiles(IApplicationBuilder app)
    {
        if (_info.Info.IsRunningFromSource)
        {
         
            var dist = _info.GetWebAppBuildPath();
            Directory.CreateDirectory(dist);
            app.UseStaticFiles(new StaticFileOptions
                {FileProvider = new PhysicalFileProvider(dist), RequestPath = ""});
        }

        app.UseStaticFiles(); // for wwwroot
    }
    
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        
        if (_info.Info.IsRunningFromSource) 
            File.WriteAllText(_info.GetWebAppApiPath(), _interop.GenerateTsRpc());
        
        return app =>
        {
            app.UseForwardedHeaders();
            if(_info.Info.IsRunningFromSource)
                app.UseCors(x => x
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .SetIsOriginAllowed(origin => true)
                    .AllowCredentials());
            
            StaticFiles(app);
            
            next(app);
            _interop.Register(app, _interceptors?.ToList());
            
            var notFound = Encoding.UTF8.GetBytes("Not found");
            app.Use((context, next) =>
            {
                if (context.Request.Method.ToUpperInvariant() != "GET"
                    || context.Request.Path
                        .ToString()?
                        .Split('/')
                        .LastOrDefault()?
                        .IndexOf('.') >= 0)
                {
                    context.Response.StatusCode = 404;
                    return context.Response.Body.WriteAsync(notFound, 0, notFound.Length);
                }

                context.Request.Path = "/index.html";
                return next();
            });
            
            StaticFiles(app);
            if (_migrationsConfig.Config != null)
                MigrationRunner.MigrateDb(_migrationsConfig.Config.ConnectionString, _info.Info.RootAssembly,
                    _migrationsConfig.Config.Type);
        };
    }
}