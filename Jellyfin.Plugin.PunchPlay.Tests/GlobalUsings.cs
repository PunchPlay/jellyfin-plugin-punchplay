global using System.Net;
global using System.Net.Http;
global using System.Security.Claims;
global using System.Text;
global using System.Text.Json;
global using Jellyfin.Data.Enums;
global using Jellyfin.Data.Entities;
global using Microsoft.AspNetCore.Authorization;
global using MediaBrowser.Controller.Authentication;
global using MediaBrowser.Controller.Entities;
global using MediaBrowser.Controller.Entities.Movies;
global using MediaBrowser.Controller.Entities.TV;
global using MediaBrowser.Controller.Library;
global using MediaBrowser.Controller.Net;
global using MediaBrowser.Controller.Session;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Mvc;
global using Microsoft.Extensions.Logging.Abstractions;
global using Moq;
global using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
