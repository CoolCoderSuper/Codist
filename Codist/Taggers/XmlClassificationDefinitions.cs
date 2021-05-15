﻿using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace Codist.Taggers
{
	static class XmlClassificationDefinitions
	{
#pragma warning disable 169, IDE0044
		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlName)]
		static ClassificationTypeDefinition XmlName;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlAttributeName)]
		static ClassificationTypeDefinition XmlAttributeName;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlAttributeQuotes)]
		static ClassificationTypeDefinition XmlAttributeQuotes;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlAttributeValue)]
		static ClassificationTypeDefinition XmlAttributeValue;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlCData)]
		static ClassificationTypeDefinition XmlCData;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlComment)]
		static ClassificationTypeDefinition XmlComment;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlDelimiter)]
		static ClassificationTypeDefinition XmlDelimiter;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlProcessingInstruction)]
		static ClassificationTypeDefinition XmlProcessingInstruction;

		[Export(typeof(ClassificationTypeDefinition))]
		[BaseDefinition(Constants.CodeFormalLanguage)]
		[Name(Constants.XmlText)]
		static ClassificationTypeDefinition XmlText;
#pragma warning restore 169
	}
}
