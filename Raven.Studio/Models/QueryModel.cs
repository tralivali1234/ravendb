using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ActiproSoftware.Windows.Controls.SyntaxEditor.IntelliPrompt;
using Raven.Studio.Features.Query;
using Raven.Studio.Infrastructure;

namespace Raven.Studio.Models
{
	public class QueryModel : ViewModel
	{
		
		#region SpatialQuery

		private bool isSpatialQuerySupported;
		public bool IsSpatialQuerySupported
		{
			get { return isSpatialQuerySupported; }
			set
			{
				isSpatialQuerySupported = value;
				OnPropertyChanged();
			}
		}

		private bool isSpatialQuery;
		public bool IsSpatialQuery
		{
			get { return isSpatialQuery; }
			set
			{
				isSpatialQuery = value;
				OnPropertyChanged();
			}
		}

		private double? latitude;
		public double? Latitude
		{
			get { return latitude; }
			set
			{
				latitude = value;
				OnPropertyChanged();
			}
		}

		private double? longitude;
		public double? Longitude
		{
			get { return longitude; }
			set
			{
				longitude = value;
				OnPropertyChanged();
			}
		}

		private double? radios;
		public double? Radios
		{
			get { return radios; }
			set
			{
				radios = value;
				OnPropertyChanged();
			}
		}

		#endregion

		private string indexName;
		public string IndexName
		{
			get
			{
				return indexName;
			}
			private set
			{
				if (string.IsNullOrWhiteSpace(value))
				{
					UrlUtil.Navigate("/indexes");
				}

				indexName = value;
				OnPropertyChanged();
				RestoreHistory();
			}
		}

		private static readonly Regex FieldsFinderRegex = new Regex(@"(^|\s)?([^\s:]+):", RegexOptions.IgnoreCase | RegexOptions.Singleline);

		private readonly BindableCollection<string> fields = new BindableCollection<string>(x => x);
		private readonly Dictionary<string, List<string>> fieldsTermsDictionary = new Dictionary<string, List<string>>();

		private static string lastQuery;
		private static string lastIndex;

		public QueryModel()
		{
			ModelUrl = "/query";

			DocumentsResult = new Observable<DocumentsModel>();
			Query = new Observable<string>();

			Query.PropertyChanged += GetTermsForUsedFields;
			CompletionProvider = new Observable<ICompletionProvider>();
			CompletionProvider.Value = new RavenQueryCompletionProvider(fields, fieldsTermsDictionary);
		}

		public override void LoadModelParameters(string parameters)
		{
			var urlParser = new UrlParser(parameters);
			IndexName = urlParser.Path.Trim('/');
			Pager.SetSkip(urlParser);

			DatabaseCommands.GetIndexAsync(IndexName)
				.ContinueOnSuccessInTheUIThread(definition =>
				{
					if (definition == null)
					{
						UrlUtil.Navigate("/NotFound?indexName=" + IndexName);
						return;
					}
					fields.Match(definition.Fields);
					IsSpatialQuerySupported = definition.Map.Contains("SpatialIndex.Generate");
				}).Catch();
		}

		public void RememberHistory()
		{
			lastIndex = IndexName;
			lastQuery = Query.Value;
		}

		public void RestoreHistory()
		{
			if (IndexName == null || lastIndex != IndexName)
				return;

			Query.Value = lastQuery;
			Execute.Execute(null);
		}

		private void GetTermsForUsedFields(object sender, PropertyChangedEventArgs e)
		{
			var text = ((Observable<string>)sender).Value;
			if (string.IsNullOrEmpty(text))
				return;

			var matches = FieldsFinderRegex.Matches(text);
			foreach (Match match in matches)
			{
				var field = match.Groups[2].Value;
				if (fieldsTermsDictionary.ContainsKey(field))
					continue;
				var terms = fieldsTermsDictionary[field] = new List<string>();
				GetTermsForField(field, terms);
			}
		}

		private void GetTermsForField(string field, List<string> terms)
		{
			DatabaseCommands.GetTermsAsync(IndexName, field, string.Empty, 1024)
				.ContinueOnSuccess(termsFromServer =>
				{
					foreach (var term in termsFromServer)
					{
						if(term.IndexOfAny(new[]{' ','\t'})  == -1)
							terms.Add(term);
						else
							terms.Add('"' + term + '"'); // quote the term
					}
				});
		}

		public Observable<ICompletionProvider> CompletionProvider { get; private set; }

		public ICommand Execute { get { return new ExecuteQueryCommand(this, DatabaseCommands); } }

		public Observable<string> Query { get; set; }

		private string error;
		public string Error
		{
			get { return error; }
			set { error = value; OnPropertyChanged(); }
		}

		public readonly PagerModel Pager = new PagerModel();

		public Observable<DocumentsModel> DocumentsResult { get; private set; }

		public string ViewTitle
		{
			get { return "Query: " + IndexName; }
		}
	}
}