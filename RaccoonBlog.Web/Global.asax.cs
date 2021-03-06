﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using System.Web.Routing;
using DataAnnotationsExtensions.ClientValidation;
using HibernatingRhinos.Loci.Common.Controllers;
using HibernatingRhinos.Loci.Common.Tasks;
using RaccoonBlog.Web.Areas.Admin.Controllers;
using RaccoonBlog.Web.Helpers.Binders;
using RaccoonBlog.Web.Infrastructure.AutoMapper;
using RaccoonBlog.Web.Infrastructure.Indexes;
using Raven.Abstractions.Logging;
using Raven.Client;
using Raven.Client.Document;
using Raven.Client.Indexes;
using Raven.Client.MvcIntegration;

namespace RaccoonBlog.Web
{
	public class MvcApplication : HttpApplication
	{
		public MvcApplication()
		{
			BeginRequest += (sender, args) =>
			{
				HttpContext.Current.Items["CurrentRequestRavenSession"] = RavenController.DocumentStore.OpenSession();
			};
			EndRequest += (sender, args) =>
			{
				using (var session = (IDocumentSession) HttpContext.Current.Items["CurrentRequestRavenSession"])
				{
					if (session == null)
						return;

					if (Server.GetLastError() != null)
						return;

					session.SaveChanges();
				}
				TaskExecutor.StartExecuting();
			};
		}

		protected void Application_Start()
		{
			AreaRegistration.RegisterAllAreas();

			FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
			new RouteConfigurator(RouteTable.Routes).Configure();

			InitializeDocumentStore();
			LogManager.GetCurrentClassLogger().Info("Started Raccoon Blog");

			ModelBinders.Binders.Add(typeof (CommentCommandOptions), new RemoveSpacesEnumBinder());
			ModelBinders.Binders.Add(typeof (Guid), new GuidBinder());

			DataAnnotationsModelValidatorProviderExtensions.RegisterValidationExtensions();

			AutoMapperConfiguration.Configure();

			RavenController.DocumentStore = DocumentStore;
			TaskExecutor.DocumentStore = DocumentStore;

			// In case the versioning bundle is installed, make sure it will version
			// only what we opt-in to version
			using (var s = DocumentStore.OpenSession())
			{
				s.Store(new
				{
					Exclude = true,
					Id = "Raven/Versioning/DefaultConfiguration",
				});
				s.SaveChanges();
			}
		}

		public static IDocumentStore DocumentStore { get; private set; }

		private static void InitializeDocumentStore()
		{
			if (DocumentStore != null) return; // prevent misuse

			DocumentStore = new DocumentStore
			                	{
			                		ConnectionStringName = "RavenDB"
			                	}.Initialize();

			TryCreatingIndexesOrRedirectToErrorPage();

			RavenProfiler.InitializeFor(DocumentStore,
			                            //Fields to filter out of the output
			                            "Email", "HashedPassword", "AkismetKey", "GoogleAnalyticsKey", "ShowPostEvenIfPrivate",
			                            "PasswordSalt", "UserHostAddress");
		}

		private static void TryCreatingIndexesOrRedirectToErrorPage()
		{
			try
			{
				IndexCreation.CreateIndexes(typeof (Tags_Count).Assembly, DocumentStore);
			}
			catch (WebException e)
			{
				var socketException = e.InnerException as SocketException;
				if(socketException == null)
					throw;

				switch (socketException.SocketErrorCode)
				{
					case SocketError.AddressNotAvailable:
					case SocketError.NetworkDown:
					case SocketError.NetworkUnreachable:
					case SocketError.ConnectionAborted:
					case SocketError.ConnectionReset:
					case SocketError.TimedOut:
					case SocketError.ConnectionRefused:
					case SocketError.HostDown:
					case SocketError.HostUnreachable:
					case SocketError.HostNotFound:
						HttpContext.Current.Response.Redirect("~/RavenNotReachable.htm");
						break;
					default:
						throw;
				}
			}
		}
	}
}